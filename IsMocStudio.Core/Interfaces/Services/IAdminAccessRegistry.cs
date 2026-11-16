namespace IsMocStudio.Core.Interfaces.Services
{
    /// <summary>
    /// Liste des e-mails administrateurs : section « AdminEmails » d'appsettings
    /// + e-mails promus automatiquement (persistés dans App_Data/admin-emails.json).
    /// </summary>
    public interface IAdminAccessRegistry
    {
        IReadOnlyCollection<string> Emails { get; }
        bool IsAdminEmail(string? email);

        /// <summary>Ajoute un e-mail à la liste persistée (sans effet s'il y figure déjà).</summary>
        Task AddAsync(string email, CancellationToken cancellationToken = default);
    }
}
