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
using System.Text.Encodings.Web;
using System.Text.Unicode;

var builder = WebApplication.CreateBuilder(args);

// 1. Base de données
builder.Services.AddDbContext<IsMocDbContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("DefaultConnection")));

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
// E-mails administrateurs : « AdminEmails » (appsettings) + promotions automatiques (App_Data/admin-emails.json).
builder.Services.AddSingleton<IAdminAccessRegistry>(serviceProvider => new AdminAccessRegistry(
    serviceProvider.GetRequiredService<IConfiguration>(),
    Path.Combine(builder.Environment.ContentRootPath, "App_Data", "admin-emails.json")));
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
        var promoted = await AdminBootstrapper.PromoteAsync(
            scope.ServiceProvider.GetRequiredService<IUnitOfWork>(),
            scope.ServiceProvider.GetRequiredService<IAdminAccessRegistry>());

        foreach (var user in promoted)
        {
            logger.LogInformation("Compte promu administrateur : {FullName} ({Email}).", $"{user.FirstName} {user.LastName}".Trim(), user.Email);
        }
    }
    catch (Exception exception)
    {
        logger.LogWarning(exception, "Promotion automatique de l'administrateur impossible (base de données indisponible ?).");
    }
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
if (app.Environment.IsDevelopment())
{
    // Swagger (documentation interactive) : jamais exposé en production. Son interface a besoin de scripts :
    // la CSP stricte ci-dessus est assouplie pour ses seules pages.
    app.Use(async (context, next) =>
    {
        if (context.Request.Path.StartsWithSegments("/swagger")) context.Response.OnStarting(() =>
        {
            context.Response.Headers.Remove("Content-Security-Policy");
            return Task.CompletedTask;
        });
        await next();
    });
    app.UseSwagger();
    app.UseSwaggerUI();
}

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