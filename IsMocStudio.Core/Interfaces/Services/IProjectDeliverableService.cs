using IsMocStudio.Core.DTOs;

namespace IsMocStudio.Core.Interfaces.Services
{
    /// <summary>
    /// Dossier de conception livré à l'équipe de développement (table ProjectDeliverables) :
    /// design-valide.html + cahier-des-charges.json, téléchargeables en zip.
    /// </summary>
    public interface IProjectDeliverableService
    {
        Task<DateTimeOffset> WriteAsync(ProjectMemoryDto memory, CancellationToken cancellationToken = default);
        Task<bool> ExistsAsync(string projectId, CancellationToken cancellationToken = default);

        /// <summary>Identifiants des projets qui ont un dossier de conception (liste du back-office en une requête).</summary>
        Task<IReadOnlySet<string>> ProjectIdsWithDeliverableAsync(CancellationToken cancellationToken = default);
        Task<(string DesignHtml, string SpecificationJson, DateTimeOffset GeneratedAt)?> ReadAsync(string projectId, CancellationToken cancellationToken = default);
        Task<byte[]?> ZipAsync(string projectId, CancellationToken cancellationToken = default);
    }
}
