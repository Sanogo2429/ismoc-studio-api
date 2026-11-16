using IsMocStudio.Core.Interfaces.Services;
using Microsoft.Extensions.Configuration;
using System.Net;
using System.Net.Mail;
using System.Threading.Tasks;

namespace IsMocStudio.Business.Services
{
    public class EmailService : IEmailService
    {
        private readonly IConfiguration _configuration;

        // On injecte la configuration pour lire le appsettings.json
        public EmailService(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        public async Task SendEmailAsync(string toEmail, string subject, string body)
        {
            // 1. Récupérer les paramètres depuis appsettings.json
            var emailSettings = _configuration.GetSection("EmailSettings");
            var smtpServer = emailSettings["SmtpServer"];
            var senderEmail = emailSettings["SenderEmail"];
            var senderPassword = emailSettings["SenderPassword"];
            var senderName = emailSettings["SenderName"];

            // Configuration incomplète : message clair au lieu d'une erreur technique.
            if (string.IsNullOrWhiteSpace(smtpServer) || string.IsNullOrWhiteSpace(senderEmail) || string.IsNullOrWhiteSpace(senderPassword)
                || !int.TryParse(emailSettings["SmtpPort"], out var smtpPort))
            {
                throw new System.InvalidOperationException("L'envoi d'e-mails n'est pas encore configuré sur le serveur. Contactez l'équipe IsMoc pour réinitialiser votre mot de passe.");
            }

            // 2. Configurer le client SMTP (le facteur)
            using var client = new SmtpClient(smtpServer, smtpPort)
            {
                Credentials = new NetworkCredential(senderEmail, senderPassword),
                EnableSsl = true // Indispensable pour Gmail
            };

            // 3. Préparer la lettre
            var mailMessage = new MailMessage
            {
                From = new MailAddress(senderEmail, senderName),
                Subject = subject,
                Body = body,
                IsBodyHtml = true // Permet d'envoyer de beaux mails en HTML plus tard
            };

            mailMessage.To.Add(toEmail);

            // 4. Envoyer !
            await client.SendMailAsync(mailMessage);
        }
    }
}