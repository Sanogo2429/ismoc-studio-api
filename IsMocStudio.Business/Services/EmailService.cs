using IsMocStudio.Core.Interfaces.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using System.Net;
using System.Net.Mail;
using System.Net.Mime;
using System.Text;
using System.Text.RegularExpressions;

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

        /// <summary>
        /// Expéditeur = identifiant technique SMTP (ex. 9a1b2c@smtp-brevo.com) : Brevo, SendGrid… REFUSENT ces
        /// envois, l'expéditeur devant être une adresse validée dans le compte (Smtp__From).
        /// </summary>
        public bool FromLooksLikeSmtpLogin =>
            From.EndsWith("smtp-brevo.com", StringComparison.OrdinalIgnoreCase)
            || (string.Equals(From, User, StringComparison.OrdinalIgnoreCase) && Server.Contains("brevo", StringComparison.OrdinalIgnoreCase));

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

    /// <summary>Échec d'envoi avec un diagnostic technique (code SMTP, réponse du serveur) sans aucun secret.</summary>
    public sealed class EmailDeliveryException : InvalidOperationException
    {
        public EmailDeliveryException(string message, string diagnostic, Exception? innerException = null)
            : base(message, innerException)
        {
            Diagnostic = diagnostic;
        }

        /// <summary>Détail technique (journal, test administrateur) : jamais affiché aux clients.</summary>
        public string Diagnostic { get; }
    }

    public class EmailService : IEmailService
    {
        /// <summary>Port SMTP bloqué ou serveur muet : échec en 20 s au lieu de 100 s (délai par défaut de SmtpClient).</summary>
        private const int SendTimeoutMilliseconds = 20_000;
        private const string NotConfiguredMessage = "L'envoi d'e-mails n'est pas encore configuré sur le serveur. Contactez l'équipe IsMoc pour réinitialiser votre mot de passe.";
        private const string FailedMessage = "L'e-mail n'a pas pu être envoyé. Réessayez dans un instant ou contactez l'équipe IsMoc.";

        private readonly IConfiguration _configuration;
        private readonly ILogger<EmailService> _logger;

        public EmailService(IConfiguration configuration, ILogger<EmailService> logger)
        {
            _configuration = configuration;
            _logger = logger;
        }

        /// <param name="body">Contenu HTML (paragraphes) : il est placé dans un gabarit complet, avec une version texte.</param>
        public async Task SendEmailAsync(string toEmail, string subject, string body)
        {
            var settings = SmtpSettings.FromConfiguration(_configuration);
            if (settings is null)
            {
                var reason = SmtpSettings.MissingReason(_configuration);
                _logger.LogError("E-mail « {Subject} » non envoyé : SMTP non configuré ({Reason}).", subject, reason);
                throw new EmailDeliveryException(NotConfiguredMessage, $"SMTP non configuré : {reason}.");
            }

            var recipient = toEmail?.Trim() ?? string.Empty;
            if (!MailAddress.TryCreate(recipient, out var recipientAddress))
            {
                _logger.LogError("E-mail « {Subject} » non envoyé : adresse destinataire invalide ({Recipient}).", subject, Mask(recipient));
                throw new EmailDeliveryException(FailedMessage, "Adresse destinataire invalide.");
            }

            if (settings.FromLooksLikeSmtpLogin)
            {
                _logger.LogWarning("Expéditeur {From} : identifiant SMTP, probablement refusé par le fournisseur. Définissez Smtp__From avec une adresse validée.", settings.From);
            }

            using var message = BuildMessage(settings, recipientAddress, subject, body);
            using var client = new SmtpClient(settings.Server, settings.Port)
            {
                Credentials = new NetworkCredential(settings.User, settings.Password),
                // STARTTLS (ports 587 / 2525). SmtpClient ne gère pas le TLS implicite du port 465.
                EnableSsl = settings.EnableSsl,
                DeliveryMethod = SmtpDeliveryMethod.Network,
                Timeout = SendTimeoutMilliseconds,
            };

            using var timeout = new CancellationTokenSource(SendTimeoutMilliseconds);
            try
            {
                await client.SendMailAsync(message, timeout.Token);
                _logger.LogInformation("E-mail « {Subject} » accepté par {Server}:{Port} pour {Recipient} (expéditeur {From}).",
                    subject, settings.Server, settings.Port, Mask(recipient), settings.From);
            }
            catch (Exception exception)
            {
                var diagnostic = Describe(exception, settings);
                // Journal complet (code SMTP, réponse du serveur, expéditeur, destinataire masqué) ; jamais d'identifiants.
                _logger.LogError(exception, "E-mail « {Subject} » NON envoyé via {Server}:{Port} (expéditeur {From}, destinataire {Recipient}) : {Diagnostic}",
                    subject, settings.Server, settings.Port, settings.From, Mask(recipient), diagnostic);
                throw new EmailDeliveryException(FailedMessage, diagnostic, exception);
            }
        }

        /// <summary>
        /// Message conforme à ce qu'attendent les relais (Brevo…) : expéditeur validé, destinataire, objet et corps
        /// en UTF-8 (accents, émojis), version texte + version HTML complète (multipart/alternative).
        /// </summary>
        private static MailMessage BuildMessage(SmtpSettings settings, MailAddress recipient, string subject, string htmlContent)
        {
            var message = new MailMessage
            {
                From = new MailAddress(settings.From, settings.FromName, Encoding.UTF8),
                Subject = string.IsNullOrWhiteSpace(subject) ? "IsMoc Studio" : subject.Trim(),
                SubjectEncoding = Encoding.UTF8,
                HeadersEncoding = Encoding.UTF8,
                BodyEncoding = Encoding.UTF8,
                // Corps principal en texte brut ; la version HTML est ajoutée en alternative.
                Body = ToPlainText(htmlContent),
                IsBodyHtml = false,
            };
            message.To.Add(recipient);
            message.ReplyToList.Add(new MailAddress(settings.From, settings.FromName, Encoding.UTF8));

            var html = AlternateView.CreateAlternateViewFromString(WrapInLayout(message.Subject, htmlContent), Encoding.UTF8, MediaTypeNames.Text.Html);
            html.TransferEncoding = TransferEncoding.QuotedPrintable;
            message.AlternateViews.Add(html);
            return message;
        }

        private static string WrapInLayout(string title, string content) =>
            "<!DOCTYPE html><html lang=\"fr\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
            + $"<title>{WebUtility.HtmlEncode(title)}</title></head>"
            + "<body style=\"margin:0;padding:24px;background:#F1F5F9;font-family:Arial,Helvetica,sans-serif;color:#0B132B;\">"
            + "<div style=\"max-width:560px;margin:0 auto;background:#FFFFFF;border-radius:12px;padding:28px;\">"
            + "<p style=\"margin:0 0 20px;font-size:18px;font-weight:bold;color:#0B132B;\">IsMoc Studio</p>"
            + $"<div style=\"font-size:15px;line-height:1.6;\">{content}</div>"
            + "<p style=\"margin:28px 0 0;font-size:12px;color:#64748B;\">Cet e-mail vous a été envoyé automatiquement par IsMoc Studio.</p>"
            + "</div></body></html>";

        private static string ToPlainText(string html)
        {
            var text = Regex.Replace(html ?? string.Empty, @"<\s*br\s*/?>", "\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, @"<\s*(/p|/div|/li|/h[1-6])\s*>", "\n\n", RegexOptions.IgnoreCase);
            text = Regex.Replace(text, "<[^>]+>", string.Empty);
            text = WebUtility.HtmlDecode(text);
            text = Regex.Replace(text, @"[ \t]+\n", "\n");
            return Regex.Replace(text, @"\n{3,}", "\n\n").Trim() + "\n\n— IsMoc Studio";
        }

        /// <summary>Diagnostic lisible d'un échec SMTP (code, réponse du serveur, cause réseau).</summary>
        private static string Describe(Exception exception, SmtpSettings settings) => exception switch
        {
            SmtpFailedRecipientException recipient => $"destinataire refusé par le serveur (code {(int)recipient.StatusCode} {recipient.StatusCode}) : {recipient.Message}",
            SmtpException smtp when smtp.StatusCode is SmtpStatusCode.MustIssueStartTlsFirst or SmtpStatusCode.ClientNotPermitted
                => $"authentification ou TLS refusés (code {(int)smtp.StatusCode} {smtp.StatusCode}) : vérifiez Smtp__User, Smtp__Pass et Smtp__EnableSsl. {smtp.Message}",
            SmtpException smtp when smtp.Message.Contains("authentic", StringComparison.OrdinalIgnoreCase) || (int)smtp.StatusCode == 535
                => $"identifiants SMTP refusés (code {(int)smtp.StatusCode}) : vérifiez Smtp__User et Smtp__Pass (clé SMTP Brevo, pas le mot de passe du compte). {smtp.Message}",
            SmtpException smtp when smtp.Message.Contains("sender", StringComparison.OrdinalIgnoreCase) || smtp.Message.Contains("from", StringComparison.OrdinalIgnoreCase)
                => $"expéditeur {settings.From} refusé (code {(int)smtp.StatusCode}) : il doit être validé chez le fournisseur. {smtp.Message} {smtp.InnerException?.Message}".Trim(),
            SmtpException smtp => $"erreur SMTP (code {(int)smtp.StatusCode} {smtp.StatusCode}) : {smtp.Message} {smtp.InnerException?.Message}".Trim(),
            OperationCanceledException => $"aucune réponse de {settings.Server}:{settings.Port} en {SendTimeoutMilliseconds / 1000} s (port bloqué ou serveur injoignable).",
            _ => $"{exception.GetType().Name} : {exception.Message} {exception.InnerException?.Message}".Trim(),
        };

        /// <summary>Adresse masquée pour les journaux (données personnelles) : « jo***@exemple.com ».</summary>
        private static string Mask(string email)
        {
            var at = email.IndexOf('@');
            if (at <= 0) return "(invalide)";
            return $"{email[..Math.Min(2, at)]}***{email[at..]}";
        }
    }
}
