using IsMocStudio.Core.Interfaces.Services;
using Microsoft.AspNetCore.Authorization; // <-- NOUVEAU
using Microsoft.AspNetCore.Mvc;

namespace IsMocStudio.Api.Controllers // ou IsMocStudio.api.Controllers selon ton dossier

{
    [Authorize(Roles = "Admin")] // <-- LE FAMEUX CADENAS EST ICI
    [Route("api/[controller]")] // L'URL sera : http://localhost:port/api/users
    [ApiController]
    public class UsersController : ControllerBase
    {
        private readonly IUserService _userService;

        // On demande (injecte) notre IUserService ici
        public UsersController(IUserService userService)
        {
            _userService = userService;
        }

        // GET: api/users
        // Permet de récupérer la liste de tous les clients
        [HttpGet]
        public async Task<IActionResult> GetAllUsers()
        {
            var users = await _userService.GetAllUsersAsync();
            return Ok(users); // 200 OK avec la liste
        }

        // GET: api/users/{id}
        // Permet de récupérer un client spécifique par son ID
        [HttpGet("{id}")]
        public async Task<IActionResult> GetUserById(int id)
        {
            try
            {
                var user = await _userService.GetUserByIdAsync(id);
                return Ok(user);
            }
            catch (Exception ex)
            {
                return NotFound(new { Message = ex.Message }); // Erreur 404 (Introuvable)
            }
        }

        // POST: api/users/email-test
        // Diagnostic administrateur : envoie un e-mail de test À L'ADMINISTRATEUR CONNECTÉ (jamais à une adresse
        // saisie, pas de relais ouvert) et renvoie le résultat SMTP exact (code, réponse du serveur) sans secret.
        [HttpPost("email-test")]
        [Microsoft.AspNetCore.RateLimiting.EnableRateLimiting(IsMocStudio.api.Security.RateLimitPolicies.PasswordReset)]
        public async Task<IActionResult> SendTestEmail(
            [FromServices] IEmailService emailService,
            [FromServices] Microsoft.Extensions.Configuration.IConfiguration configuration)
        {
            var adminEmail = User.FindFirst(System.Security.Claims.ClaimTypes.Email)?.Value;
            if (string.IsNullOrWhiteSpace(adminEmail)) return BadRequest(new { Message = "Adresse e-mail du compte introuvable dans la session." });

            var settings = IsMocStudio.Business.Services.SmtpSettings.FromConfiguration(configuration);
            var smtp = settings is null ? null : new { settings.Server, settings.Port, Sender = settings.From, settings.EnableSsl, SenderIsSmtpLogin = settings.FromLooksLikeSmtpLogin };

            try
            {
                await emailService.SendEmailAsync(adminEmail, "Test d'envoi d'e-mails - IsMoc Studio",
                    "<p>Bonjour,</p><p>Cet e-mail de test confirme que l'envoi d'e-mails d'IsMoc Studio fonctionne.</p>");
                return Ok(new { Sent = true, Message = "E-mail de test accepté par le serveur SMTP. Vérifiez votre boîte de réception (et les spams).", Smtp = smtp });
            }
            catch (IsMocStudio.Business.Services.EmailDeliveryException exception)
            {
                return StatusCode(StatusCodes.Status502BadGateway, new { Sent = false, Message = exception.Diagnostic, Smtp = smtp });
            }
        }

        // DELETE: api/users/{id}
        // Permet de supprimer un client
        [HttpDelete("{id}")]
        public async Task<IActionResult> DeleteUser(int id)
        {
            try
            {
                await _userService.DeleteUserAsync(id);
                return NoContent(); // 204 No Content : Action réussie, pas de données à renvoyer
            }
            catch (Exception ex)
            {
                return NotFound(new { Message = ex.Message });
            }
        }
    }
}