namespace IsMocStudio.Core.Interfaces.Services
{
    /// <summary>
    /// Liste des e-mails administrateurs : UNIQUEMENT la section « AdminEmails » de la configuration
    /// (appsettings.json, variables d'environnement AdminEmails__0, AdminEmails__1…).
    /// Seule source de vérité du rôle Admin : aucune promotion automatique, aucune liste écrite au runtime.
    /// </summary>
    public interface IAdminAccessRegistry
    {
        IReadOnlyCollection<string> Emails { get; }
        bool IsAdminEmail(string? email);
    }
}
