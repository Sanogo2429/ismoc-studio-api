using IsMocStudio.Core.Interfaces.Services;
using Microsoft.Extensions.Configuration;

namespace IsMocStudio.Business.Services
{
    /// <summary>
    /// E-mails administrateurs = section « AdminEmails » de la configuration, et rien d'autre.
    /// (L'ancienne liste persistée App_Data/admin-emails.json, alimentée automatiquement par tout compte
    /// déjà Admin en base, n'est plus lue : un rôle Admin ne peut plus « s'auto-entretenir ».)
    /// </summary>
    public sealed class AdminAccessRegistry : IAdminAccessRegistry
    {
        private readonly HashSet<string> _emails;

        public AdminAccessRegistry(IConfiguration configuration)
        {
            _emails = new HashSet<string>(
                configuration.GetSection("AdminEmails").GetChildren()
                    .Select(child => Normalize(child.Value))
                    .Where(email => email.Length > 0),
                StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyCollection<string> Emails => _emails.OrderBy(email => email).ToList();

        public bool IsAdminEmail(string? email)
        {
            var normalized = Normalize(email);
            return normalized.Length > 0 && _emails.Contains(normalized);
        }

        private static string Normalize(string? email) => email?.Trim().ToLowerInvariant() ?? string.Empty;
    }
}
