using IsMocStudio.Core.DTOs;
using IsMocStudio.Core.Interfaces.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Google;
using Microsoft.AspNetCore.RateLimiting;
using System.Security.Claims;
using System;
using IsMocStudio.api.Security;

namespace IsMocStudio.Api.Controllers
{
    [Route("api/[controller]")] // L'URL sera : http://localhost:port/api/auth
    [ApiController] // Indique que c'est un contrôleur d'API REST
    public class AuthController : ControllerBase
    {
        private readonly IAuthService _authService;
        private readonly ILogger<AuthController> _logger;
        private readonly IConfiguration _configuration;

        public AuthController(IAuthService authService, ILogger<AuthController> logger, IConfiguration configuration)
        {
            _authService = authService;
            _logger = logger;
            _configuration = configuration;
        }

        /// <summary>Connexion par SMS : désactivée tant qu'un vrai fournisseur SMS n'est pas branché (« Features:SmsLogin »).</summary>
        private bool SmsLoginEnabled => _configuration.GetValue<bool>("Features:SmsLogin");

        // POST: api/auth/register
        [HttpPost("register")]
        [EnableRateLimiting(RateLimitPolicies.Auth)]
        public async Task<IActionResult> Register([FromBody] RegisterRequestDto request)
        {
            try
            {
                var response = await _authService.RegisterAsync(request);
                return Ok(response);
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { Message = ex.Message }); // ex. e-mail déjà utilisé, mot de passe trop faible
            }
            catch (Exception ex)
            {
                // Erreur inattendue (base…) : journalisée, jamais renvoyée telle quelle au visiteur.
                _logger.LogError(ex, "Échec technique de l'inscription.");
                return StatusCode(StatusCodes.Status500InternalServerError, new { Message = "L'inscription a échoué. Réessayez dans un instant." });
            }
        }

        // POST: api/auth/login
        [HttpPost("login")]
        [EnableRateLimiting(RateLimitPolicies.Auth)]
        public async Task<IActionResult> Login([FromBody] LoginRequestDto request)
        {
            try
            {
                var response = await _authService.LoginAsync(request);
                return Ok(response);
            }
            catch (Exception ex)
            {
                // Message unique : ne révèle ni si le compte existe, ni une éventuelle erreur technique.
                if (ex.Message != "Identifiants incorrects.") _logger.LogError(ex, "Échec technique de la connexion.");
                return Unauthorized(new { Message = "Identifiants incorrects." });
            }
        }

        // POST: api/auth/forgot-password
        [HttpPost("forgot-password")]
        [EnableRateLimiting(RateLimitPolicies.PasswordReset)]
        public async Task<IActionResult> ForgotPassword([FromBody] ForgotPasswordRequestDto request)
        {
            try
            {
                await _authService.ForgotPasswordAsync(request.Email);
                // Réponse identique que le compte existe ou non : on ne révèle pas les adresses inscrites.
                return Ok(new { Message = "Si un compte est associé à cette adresse, un e-mail avec un mot de passe temporaire vient d'être envoyé." });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { Message = ex.Message });
            }
            catch (InvalidOperationException ex)
            {
                // Envoi SMTP impossible : le mot de passe actuel n'a pas été modifié (message sans détail technique).
                return StatusCode(StatusCodes.Status503ServiceUnavailable, new { Message = ex.Message });
            }
        }

        /// <summary>Clé (dans l'état chiffré envoyé à Google) de l'adresse du site à rouvrir après la connexion.</summary>
        public const string FrontendOriginItem = "frontendOrigin";

        // GET: api/auth/google-login?returnUrl=https://ismoc-app.loca.lt
        [HttpGet("google-login")]
        public IActionResult GoogleLogin([FromQuery] string? returnUrl, [FromServices] FrontendOrigins frontendOrigins)
        {
            // On prépare l'adresse de retour (où Google renverra le client après sa connexion)
            var properties = new AuthenticationProperties
            {
                RedirectUri = Url.Action("GoogleCallback")
            };
            // Site d'origine (ordinateur, tunnel, mobile…) : uniquement s'il fait partie des adresses autorisées.
            // (paramètre returnUrl envoyé par le front, à défaut en-têtes Origin / Referer, à défaut « Frontend:BaseUrl »)
            properties.Items[FrontendOriginItem] = frontendOrigins.ResolveFromRequest(returnUrl, Request);

            // On lance le "Challenge" : ça va rediriger le client vers l'écran de connexion Google
            return Challenge(properties, GoogleDefaults.AuthenticationScheme);
        }

        [HttpGet("google-callback")]
        public async Task<IActionResult> GoogleCallback([FromServices] FrontendOrigins frontendOrigins)
        {
            // 1. On intercepte la réponse renvoyée par Google
            var authenticateResult = await HttpContext.AuthenticateAsync(GoogleDefaults.AuthenticationScheme);
            string? requestedOrigin = null;
            authenticateResult.Properties?.Items.TryGetValue(FrontendOriginItem, out requestedOrigin);
            var frontend = frontendOrigins.Resolve(requestedOrigin);

            if (!authenticateResult.Succeeded)
            {
                return Redirect($"{frontend}/connexion?error=google");
            }

            // 2. On ouvre le "paquet" pour extraire les informations du client
            var email = authenticateResult.Principal?.FindFirst(ClaimTypes.Email)?.Value;
            var firstName = authenticateResult.Principal?.FindFirst(ClaimTypes.GivenName)?.Value;
            var lastName = authenticateResult.Principal?.FindFirst(ClaimTypes.Surname)?.Value;

            // Le cookie temporaire de l'aller-retour Google n'est plus utile : on le supprime.
            await HttpContext.SignOutAsync(Microsoft.AspNetCore.Authentication.Cookies.CookieAuthenticationDefaults.AuthenticationScheme);

            if (string.IsNullOrWhiteSpace(email))
            {
                return Redirect($"{frontend}/connexion?error=google");
            }

            // 3. On appelle notre méthode métier (qui va créer le compte si besoin et générer le Token)
            var result = await _authService.GoogleLoginAsync(email, firstName ?? "Utilisateur", lastName ?? "Google");

            // 4. Retour sur le site d'origine. Le jeton passe dans le FRAGMENT (#…) et non dans la query (?…) :
            //    un fragment n'est jamais envoyé aux serveurs (journaux, proxys) ni transmis dans l'en-tête Referer.
            var redirectUrl = $"{frontend}/login#token={Uri.EscapeDataString(result.Token)}&name={Uri.EscapeDataString(result.FullName)}";
            return Redirect(redirectUrl);
        }

        // POST: api/auth/send-sms
        [HttpPost("send-sms")]
        [EnableRateLimiting(RateLimitPolicies.Auth)]
        public async Task<IActionResult> SendSms([FromBody] SendSmsRequestDto request)
        {
            if (!SmsLoginEnabled) return NotFound();
            try
            {
                await _authService.SendSmsOtpAsync(request.PhoneNumber);
                return Ok(new { Message = "Code SMS envoyé." });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Message = ex.Message });
            }
        }

        // POST: api/auth/verify-sms
        [HttpPost("verify-sms")]
        [EnableRateLimiting(RateLimitPolicies.Auth)]
        public async Task<IActionResult> VerifySms([FromBody] VerifySmsRequestDto request)
        {
            if (!SmsLoginEnabled) return NotFound();
            try
            {
                // On vérifie le code et on récupère le Token JWT !
                var response = await _authService.VerifySmsOtpAsync(request.PhoneNumber, request.Code);
                return Ok(response);
            }
            catch (Exception ex)
            {
                return BadRequest(new { Message = ex.Message }); // Erreur si le code est faux ou expiré
            }
        }

        // ------------------------------------------------------------------
        // Compte connecté (onglet Profil)
        // ------------------------------------------------------------------

        /// <summary>Méthode de connexion de la session (claim « amr », renommé par la validation JWT .NET).</summary>
        private string? CurrentAuthMethod() =>
            User.FindFirst("amr")?.Value ?? User.FindFirst("http://schemas.microsoft.com/claims/authnmethodsreferences")?.Value;

        // GET: api/auth/me
        [Authorize]
        [HttpGet("me")]
        public async Task<IActionResult> Me()
        {
            if (!TryGetUserId(out var userId)) return Unauthorized(new { Message = "Utilisateur non identifié." });
            try
            {
                return Ok(await _authService.GetProfileAsync(userId, CurrentAuthMethod()));
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { Message = ex.Message });
            }
        }

        // PUT: api/auth/profile
        [Authorize]
        [HttpPut("profile")]
        public async Task<IActionResult> UpdateProfile([FromBody] UpdateProfileRequestDto request)
        {
            if (!TryGetUserId(out var userId)) return Unauthorized(new { Message = "Utilisateur non identifié." });
            try
            {
                return Ok(await _authService.UpdateProfileAsync(userId, request, CurrentAuthMethod()));
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { Message = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { Message = ex.Message });
            }
        }

        // POST: api/auth/change-password
        [Authorize]
        [HttpPost("change-password")]
        public async Task<IActionResult> ChangePassword([FromBody] ChangePasswordRequestDto request)
        {
            if (!TryGetUserId(out var userId)) return Unauthorized(new { Message = "Utilisateur non identifié." });
            try
            {
                await _authService.ChangePasswordAsync(userId, request);
                return Ok(new { Message = "Votre mot de passe a été modifié." });
            }
            catch (UnauthorizedAccessException ex)
            {
                // 400 et non 401 : le jeton est valide, seul le mot de passe saisi est faux
                // (un 401 déconnecterait l'utilisateur côté frontend).
                return BadRequest(new { Message = ex.Message });
            }
            catch (ArgumentException ex)
            {
                return BadRequest(new { Message = ex.Message });
            }
            catch (KeyNotFoundException ex)
            {
                return NotFound(new { Message = ex.Message });
            }
        }

        private bool TryGetUserId(out int userId) =>
            int.TryParse(User.FindFirst(ClaimTypes.NameIdentifier)?.Value, out userId);
    }
}