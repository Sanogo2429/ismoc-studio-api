using IsMocStudio.Core.Interfaces.Services;
using Microsoft.Extensions.Logging;
using System.Threading.Tasks;

namespace IsMocStudio.Business.Services
{
    public class SmsService : ISmsService
    {
        private readonly ILogger<SmsService> _logger;

        // On injecte le logger pour afficher le faux SMS dans la console
        public SmsService(ILogger<SmsService> logger)
        {
            _logger = logger;
        }

        public Task SendSmsAsync(string phoneNumber, string message)
        {
            // Simulation de l'envoi du SMS dans la console de développement
            _logger.LogInformation("📱 ======== ENVOI DE SMS (SIMULATION) ========");
            _logger.LogInformation($"Numéro : {phoneNumber}");
            _logger.LogInformation($"Message : {message}");
            _logger.LogInformation("===============================================");

            return Task.CompletedTask;
        }
    }
}