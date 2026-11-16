using Microsoft.EntityFrameworkCore;
using IsMocStudio.Data.Context;
using IsMocStudio.Core.Interfaces.Repositories;
using IsMocStudio.Core.Interfaces.Services;
using IsMocStudio.Business.Services;
using IsMocStudio.Business.Helpers;
using IsMocStudio.api.Hubs;
using IsMocStudio.api.Security;
using IsMocStudio.Api.Controllers;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.OpenApi.Models;
using System.Threading.RateLimiting;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Unicode;

var builder = WebApplication.CreateBuilder(args);

// 1. Base de données
builder.Services.AddDbContext<IsMocDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

// 2. Injection de dépendances
builder.Services.AddScoped<IUnitOfWork, IsMocStudio.Data.UnitOfWork.UnitOfWork>();
builder.Services.AddScoped<IAuthService, AuthService>();
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddScoped<IProjectService, ProjectService>();
builder.Services.AddScoped<IAiService, AiService>();
builder.Services.AddScoped<IOpenAiMvpService, OpenAiMvpService>();

// Client HTTP dédié à l'API Messages d'Anthropic (moteur UI prioritaire) : délai porté à 4 minutes
// pour ne pas couper les générations de maquettes riches (16 384 tokens de sortie).
builder.Services.AddHttpClient<AnthropicMessagesClient>(client =>
{
    client.BaseAddress = new Uri("https://api.anthropic.com/");
    client.Timeout = OpenAiMvpService.GenerationTimeout;
});
builder.Services.AddSingleton<IProjectMemoryService>(_ => new ProjectMemoryService(
    Path.Combine(builder.Environment.ContentRootPath, "App_Data", "Memories")));
builder.Services.AddSingleton<IProjectDeliverableService>(_ => new ProjectDeliverableService(
    Path.Combine(builder.Environment.ContentRootPath, "App_Data", "Deliverables")));
builder.Services.AddSingleton<IProjectAttachmentService>(_ => new ProjectAttachmentService(
    Path.Combine(builder.Environment.ContentRootPath, "App_Data", "Attachments")));

// Temps réel : messagerie, présence et signalisation WebRTC.
builder.Services.AddSingleton<ProjectPresenceTracker>();
builder.Services.AddSignalR(options =>
{
    options.MaximumReceiveMessageSize = 128 * 1024; // offres SDP WebRTC
}).AddJsonProtocol(options =>
{
    options.PayloadSerializerOptions.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All);
});
// E-mails administrateurs : uniquement « AdminEmails » (appsettings.json / variables AdminEmails__0…).
builder.Services.AddSingleton<IAdminAccessRegistry>(serviceProvider => new AdminAccessRegistry(
    serviceProvider.GetRequiredService<IConfiguration>()));
// Crédits IA gratuits par compte + limite anti-spam (« AiQuotas » dans appsettings.json).
builder.Services.AddSingleton<IAiQuotaService>(serviceProvider => new AiQuotaService(
    serviceProvider.GetRequiredService<IConfiguration>(),
    Path.Combine(builder.Environment.ContentRootPath, "App_Data", "ai-quotas.json")));
builder.Services.AddHttpClient();
builder.Services.AddScoped<IEmailService, EmailService>();
builder.Services.AddScoped<ISmsService, SmsService>();
builder.Services.AddMemoryCache();
builder.Services.AddScoped<TokenHelper>();

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Encoder = JavaScriptEncoder.Create(UnicodeRanges.All);
});
builder.Services.AddEndpointsApiExplorer();

// --- Ajout de la configuration CORS ---
// Adresses du frontend autorisées (CORS + retour de la connexion Google) : voir Security/FrontendOrigins.cs.
var frontendOrigins = new FrontendOrigins(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(frontendOrigins);
builder.Services.AddCors(options =>
{
    options.AddPolicy("AllowReactApp",
        policy =>
        {
            // AllowCredentials est requis par SignalR (négociation + WebSocket).
            policy.SetIsOriginAllowed(frontendOrigins.IsAllowed)
                  .AllowAnyHeader()
                  .AllowAnyMethod()
                  .AllowCredentials();
        });
});

// --- 3. Configuration de l'Authentification JWT & Google ---
// Clé JWT : User Secrets (local) ou variable d'environnement JwtSettings__Secret (production).
// Absente ou trop courte → l'API refuse de démarrer (jamais de clé par défaut).
var jwtValidationParameters = TokenHelper.CreateValidationParameters(builder.Configuration);
var isDevelopment = builder.Environment.IsDevelopment();

builder.Services.AddAuthentication(options =>
{
    options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    options.DefaultSignInScheme = CookieAuthenticationDefaults.AuthenticationScheme;
})
.AddCookie(options =>
{
    // Cookie temporaire de l'aller-retour Google uniquement (l'API n'utilise pas de session par cookie).
    options.Cookie.SameSite = SameSiteMode.Lax;
    options.Cookie.HttpOnly = true;
    options.Cookie.SecurePolicy = isDevelopment ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
    options.ExpireTimeSpan = TimeSpan.FromMinutes(10);
})
.AddJwtBearer(options =>
{
    // HTTPS obligatoire hors développement.
    options.RequireHttpsMetadata = !isDevelopment;
    options.SaveToken = false;
    // Signature HMAC-SHA256, émetteur, audience et expiration vérifiés (voir TokenHelper).
    options.TokenValidationParameters = jwtValidationParameters;

    // Les WebSockets ne peuvent pas envoyer d'en-tête Authorization : SignalR transmet le JWT
    // dans la query string (access_token), accepté uniquement pour le hub.
    options.Events = new JwtBearerEvents
    {
        OnMessageReceived = context =>
        {
            var accessToken = context.Request.Query["access_token"];
            if (!string.IsNullOrEmpty(accessToken) && context.HttpContext.Request.Path.StartsWithSegments(ProjectCommunicationHub.Route))
            {
                context.Token = accessToken;
            }

            return Task.CompletedTask;
        }
    };
})
.AddGoogle(options =>
{
    options.ClientId = builder.Configuration["Authentication:Google:ClientId"] ?? "FAUX_CLIENT_ID_POUR_TEST";
    options.ClientSecret = builder.Configuration["Authentication:Google:ClientSecret"] ?? "FAUX_SECRET_POUR_TEST";

    // Cookie anti-falsification de l'aller-retour Google : doit revenir sur le retour cross-site
    // (accounts.google.com → API), y compris à travers un tunnel HTTPS (localtunnel) et sur mobile.
    options.CorrelationCookie.SameSite = SameSiteMode.None;
    options.CorrelationCookie.SecurePolicy = CookieSecurePolicy.Always;

    // Échec ou annulation (ex. « Correlation failed ») : retour propre sur la page de connexion du site
    // au lieu de la page d'exception ASP.NET.
    options.Events.OnRemoteFailure = context =>
    {
        context.Properties ??= new AuthenticationProperties();
        context.Properties.Items.TryGetValue(AuthController.FrontendOriginItem, out var requestedOrigin);
        var frontend = context.HttpContext.RequestServices.GetRequiredService<FrontendOrigins>().Resolve(requestedOrigin);
        context.HttpContext.RequestServices.GetRequiredService<ILogger<Program>>()
            .LogWarning(context.Failure, "Connexion Google échouée.");
        context.Response.Redirect($"{frontend}/connexion?error=google");
        context.HandleResponse();
        return Task.CompletedTask;
    };
});

// --- Limitation de débit : anti brute-force sur la connexion, l'inscription et la réinitialisation ---
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.ContentType = "application/json; charset=utf-8";
        await context.HttpContext.Response.WriteAsync(
            "{\"message\":\"Trop de tentatives. Patientez quelques minutes avant de réessayer.\"}", cancellationToken);
    };

    static string ClientKey(HttpContext context) => context.Connection.RemoteIpAddress?.ToString() ?? "inconnu";

    // Connexion / inscription / SMS : 10 essais par minute et par adresse IP.
    options.AddPolicy(RateLimitPolicies.Auth, context => RateLimitPartition.GetFixedWindowLimiter(ClientKey(context),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 10, Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));

    // Mot de passe oublié : 5 demandes par quart d'heure et par adresse IP.
    options.AddPolicy(RateLimitPolicies.PasswordReset, context => RateLimitPartition.GetFixedWindowLimiter(ClientKey(context),
        _ => new FixedWindowRateLimiterOptions { PermitLimit = 5, Window = TimeSpan.FromMinutes(15), QueueLimit = 0 }));
});

// --- Derrière un hébergeur / reverse proxy (Azure, Nginx, IIS…) : vraie IP cliente et schéma HTTPS ---
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    // Proxys de confiance : à restreindre à l'adresse du proxy si elle est connue (KnownProxies / KnownNetworks).
    options.KnownNetworks.Clear();
    options.KnownProxies.Clear();
});

builder.Services.AddHsts(options =>
{
    options.MaxAge = TimeSpan.FromDays(365);
    options.IncludeSubDomains = true;
});

builder.Services.AddHealthChecks();

// --- 4. Configuration de Swagger pour accepter le Token ---
builder.Services.AddSwaggerGen(c =>
{
    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Description = "Autorisation JWT. Entrez 'Bearer' [espace] puis votre token.",
        Name = "Authorization",
        In = ParameterLocation.Header,
        Type = SecuritySchemeType.ApiKey,
        Scheme = "Bearer"
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference { Type = ReferenceType.SecurityScheme, Id = "Bearer" }
            },
            new string[] {}
        }
    });
});

var app = builder.Build();

// --- Promotion automatique des administrateurs (aucune manipulation manuelle de la base) ---
// Seuls les comptes EXISTANTS dont l'e-mail figure dans « AdminEmails » passent en rôle Admin.
// (Plus de promotion par nom affiché ni du « premier inscrit » : n'importe qui pourrait en profiter.)
// La base n'est pas migrée ici : une base injoignable n'empêche pas l'API de démarrer.
using (var scope = app.Services.CreateScope())
{
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    try
    {
        // Rôles alignés sur « AdminEmails » : promotion des comptes listés, rétrogradation en Client des autres Admin.
        var roles = await AdminBootstrapper.SyncRolesAsync(
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            scope.ServiceProvider.GetRequiredService<IAdminAccessRegistry>());

        foreach (var user in roles.Promoted) logger.LogInformation("Compte promu administrateur : {UserId}.", user.Id);
        foreach (var user in roles.Demoted) logger.LogWarning("Compte rétrogradé en Client (absent de AdminEmails) : {UserId}.", user.Id);
    }
    catch (Exception exception)
    {
        logger.LogWarning(exception, "Synchronisation des rôles impossible (base de données indisponible ?).");
    }

    // Envoi d'e-mails (inscription, mot de passe oublié) : état visible dès le démarrage dans les journaux.
    var smtp = SmtpSettings.FromConfiguration(app.Configuration);
    if (smtp is null)
        logger.LogWarning("Envoi d'e-mails désactivé ({Reason}) : définissez Smtp__Server, Smtp__Port, Smtp__User, Smtp__Pass et Smtp__From.",
            SmtpSettings.MissingReason(app.Configuration));
    else
        logger.LogInformation("Envoi d'e-mails configuré via {Server}:{Port} (expéditeur {From}).", smtp.Server, smtp.Port, smtp.From);
}

app.UseForwardedHeaders();

// Erreurs : détail technique en développement uniquement ; en production, réponse générique (aucune fuite).
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsync("{\"message\":\"Une erreur interne est survenue. Réessayez dans un instant.\"}");
    }));
    // HTTPS strict mémorisé par le navigateur (jamais en local : le certificat de développement change).
    app.UseHsts();
}

// En-têtes de sécurité sur toutes les réponses de l'API.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers.XContentTypeOptions = "nosniff";
    headers.XFrameOptions = "DENY";
    headers["Referrer-Policy"] = "no-referrer";
    headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
    headers["Cross-Origin-Opener-Policy"] = "same-origin";
    // L'API ne sert que du JSON et des fichiers : aucune ressource active autorisée.
    headers.ContentSecurityPolicy = "default-src 'none'; frame-ancestors 'none'";
    await next();
});

app.Use(async (context, next) =>
{
    context.Response.OnStarting(() =>
    {
        if (context.Response.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) == true
            && !context.Response.ContentType.Contains("charset=", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.ContentType += "; charset=utf-8";
        }

        return Task.CompletedTask;
    });

    await next();
});

// Configuration du pipeline HTTP
// Swagger (documentation interactive), dans tous les environnements, affiché à la racine de l'API (« / »).
// Son interface a besoin de scripts et de styles : la CSP stricte ci-dessus est remplacée, pour ses seules
// pages, par une CSP limitée aux fichiers de l'API elle-même. Les routes de l'API gardent la CSP stricte.
static bool IsApiRoute(PathString path) =>
    path.StartsWithSegments("/api") || path.StartsWithSegments("/hubs")
    || path.StartsWithSegments("/health") || path.StartsWithSegments("/signin-google");

app.Use(async (context, next) =>
{
    if (!IsApiRoute(context.Request.Path)) context.Response.OnStarting(() =>
    {
        context.Response.Headers["Content-Security-Policy"] =
            "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; " +
            "img-src 'self' data:; frame-ancestors 'none'";
        return Task.CompletedTask;
    });
    await next();
});

// Protection de Swagger par authentification basique (Basic Auth) : toutes les pages hors routes de l'API
// (« / », « /index.html », ses scripts/styles et « /swagger/v1/swagger.json »).
// Identifiants : « BasicAuth:Username » / « BasicAuth:Password » (User Secrets en local, variables
// d'environnement BasicAuth__Username / BasicAuth__Password en production). Aucun mot de passe par défaut
// n'est écrit dans le code : sans identifiants valides configurés, Swagger est FERMÉ (404) hors développement.
const int SwaggerPasswordMinLength = 12;
var swaggerUsername = app.Configuration["BasicAuth:Username"];
var swaggerPassword = app.Configuration["BasicAuth:Password"];
var swaggerCredentialsConfigured = !string.IsNullOrWhiteSpace(swaggerUsername)
    && !string.IsNullOrEmpty(swaggerPassword) && swaggerPassword.Length >= SwaggerPasswordMinLength;
if (!swaggerCredentialsConfigured && !app.Environment.IsDevelopment())
{
    app.Logger.LogWarning(
        "Swagger désactivé : définissez BasicAuth__Username et BasicAuth__Password ({MinLength} caractères minimum).",
        SwaggerPasswordMinLength);
}

app.Use(async (context, next) =>
{
    if (IsApiRoute(context.Request.Path))
    {
        await next();
        return;
    }

    if (!swaggerCredentialsConfigured)
    {
        // Développement sans identifiants : Swagger reste ouvert sur le poste local. Production : fermé.
        if (app.Environment.IsDevelopment()) await next();
        else context.Response.StatusCode = StatusCodes.Status404NotFound;
        return;
    }

    if (TryReadBasicCredentials(context.Request, out var username, out var password)
        & SecureEquals(username, swaggerUsername!)
        & SecureEquals(password, swaggerPassword!))
    {
        await next();
        return;
    }

    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
    context.Response.Headers.WWWAuthenticate = "Basic realm=\"IsMoc Studio API\", charset=\"UTF-8\"";
});

// Lit l'en-tête « Authorization: Basic base64(utilisateur:mot-de-passe) ».
static bool TryReadBasicCredentials(HttpRequest request, out string username, out string password)
{
    username = password = string.Empty;
    var header = request.Headers.Authorization.ToString();
    if (!header.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
    try
    {
        var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(header["Basic ".Length..].Trim()));
        var separator = decoded.IndexOf(':');
        if (separator < 0) return false;
        username = decoded[..separator];
        password = decoded[(separator + 1)..];
        return true;
    }
    catch (FormatException)
    {
        return false;
    }
}

// Comparaison en temps constant (sur les empreintes SHA-256) : ni le contenu ni la longueur ne fuient par le temps de réponse.
static bool SecureEquals(string candidate, string expected) =>
    CryptographicOperations.FixedTimeEquals(
        SHA256.HashData(Encoding.UTF8.GetBytes(candidate)),
        SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

app.UseSwagger();
app.UseSwaggerUI(options =>
{
    options.SwaggerEndpoint("/swagger/v1/swagger.json", "IsMoc Studio API v1");
    options.RoutePrefix = string.Empty;
});

app.UseHttpsRedirection();

// --- Activation du CORS (doit être avant UseAuthentication) ---
app.UseCors("AllowReactApp");

// --- 5. Activer l'Authentification AVANT l'Autorisation ---
app.UseAuthentication();
app.UseAuthorization();
app.UseRateLimiter();

app.MapControllers();
// Temps réel : JWT obligatoire ([Authorize] sur le hub) ; la connexion est coupée dès que le jeton expire.
app.MapHub<ProjectCommunicationHub>(ProjectCommunicationHub.Route, options =>
{
    options.CloseOnAuthenticationExpiration = true;
});
// Sonde de disponibilité pour l'hébergeur (aucune donnée exposée).
app.MapHealthChecks("/health");

app.Run();