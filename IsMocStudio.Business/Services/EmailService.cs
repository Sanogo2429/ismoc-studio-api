using IsMocStudio.Core.Interfaces.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Mail;

namespace IsMocStudio.Business.Services
{
    /// <summary>
    /// Paramètres SMTP, lus en priorité dans la section « Smtp » : variables d'environnement Smtp__Server,
    /// Smtp__Port, Smtp__User, Smtp__Pass, Smtp__From (équivalentes à Smtp:Server… pour .NET), facultatives
    /// Smtp__FromName et Smtp__EnableSsl. En local, l'ancienne section « EmailSettings » sert de repli.
    /// Seuls le serveur et le mot de passe sont indispensables : port 2525 par défaut (non bloqué par Render),
    /// identifiant = expéditeur et expéditeur = identifiant si l'un des deux manque.
    /// </summary>
    public sealed record SmtpSettings(string Server, int Port, string User, string Password, string From, string FromName, bool EnableSsl)
    {
        public const int DefaultPort = 2525;

        public static SmtpSettings? FromConfiguration(IConfiguration configuration)
        {
            var smtp = configuration.GetSection("Smtp");
            var legacy = configuration.GetSection("EmailSettings");
            var smtpConfigured = !string.IsNullOrWhiteSpace(smtp["Server"]);

            // La section « Smtp » prime ; les anciens réglages ne complètent que si elle est absente (pas de mélange).
            string? Read(string smtpKey, string legacyKey) => smtpConfigured ? First(smtp[smtpKey]) : First(smtp[smtpKey], legacy[legacyKey]);

            var server = Read("Server", "SmtpServer");
            var password = Read("Pass", "SenderPassword");
            if (server is null || password is null) return null;

            var user = First(Read("User", "SenderEmail"), Read("From", "SenderEmail"));
            var from = First(Read("From", "SenderEmail"), user);
            if (user is null || from is null || !MailAddress.TryCreate(from, out _)) return null;

            var port = int.TryParse(Read("Port", "SmtpPort"), out var parsedPort) && parsedPort is > 0 and <= 65535
                ? parsedPort
                : DefaultPort;
            var fromName = Read("FromName", "SenderName") ?? "IsMoc Studio";
            var enableSsl = !bool.TryParse(smtp["EnableSsl"], out var ssl) || ssl;

            return new SmtpSettings(server, port, user, password, from, fromName, enableSsl);
        }

        /// <summary>Raison lisible d'une configuration inutilisable (pour le journal de démarrage).</summary>
        public static string MissingReason(IConfiguration configuration)
        {
            var smtp = configuration.GetSection("Smtp");
            if (string.IsNullOrWhiteSpace(smtp["Server"])) return "Smtp__Server manquant";
            if (string.IsNullOrWhiteSpace(smtp["Pass"])) return "Smtp__Pass manquant";
            if (string.IsNullOrWhiteSpace(smtp["User"]) && string.IsNullOrWhiteSpace(smtp["From"])) return "Smtp__User ou Smtp__From manquant";
            return "Smtp__From n'est pas une adresse e-mail valide";
        }

        private static string? First(params string?[] values) =>
            values.Select(value => value?.Trim()).FirstOrDefault(value => !string.IsNullOrEmpty(value));
    }

    public class EmailService : IEmailService
    {
        /// <summary>Port SMTP bloqué ou serveur muet : échec en 20 s au lieu de 100 s (délai par défaut de SmtpClient).</summary>
        private const int SendTimeoutMilliseconds = 20_000;

        private readonly IConfiguration _configuration;
        private readonly ILogger<EmailService> _logger;

        public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        public async Task SendEmailAsync(string toEmail, string subject, string body)
        {
            var settings = SmtpSettings.FromConfiguration(_configuration)
                ?? throw new InvalidOperationException("L'envoi d'e-mails n'est pas encore configuré sur le serveur. Contactez l'équipe IsMoc pour réinitialiser votre mot de passe.");

            using var client = new SmtpClient(settings.Server, settings.Port)
            {
                Credentials = new NetworkCredential(settings.User, settings.Password),
                // STARTTLS (ports 587 / 2525). SmtpClient ne gère pas le TLS implicite du port 465.
                EnableSsl = settings.EnableSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = SendTimeoutMilliseconds,
            };

            using var message = new MailMessage
            {
                From = new MailAddress(settings.From, settings.FromName),
                Subject = subject,
                Body = body,
                IsBodyHtml = true,
            };
            message.To.Add(toEmail);

            using var timeout = new CancellationTokenSource(SendTimeoutMilliseconds);
            try
            {
                await client.SendMailAsync(message, timeout.Token);
            }
            catch (Exception exception) when (exception is SmtpException or OperationCanceledException or IOException)
            {
                // Journal technique (serveur/port, jamais d'identifiants) ; message neutre pour l'appelant.
                _logger.LogError(exception, "Échec de l'envoi d'un e-mail via {Server}:{Port}.", settings.Server, settings.Port);
                throw new InvalidOperationException("L'e-mail n'a pas pu être envoyé. Réessayez dans un instant ou contactez l'équipe IsMoc.", exception);
            }
        }
    }
}
