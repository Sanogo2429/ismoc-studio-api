using IsMocStudio.Core.DTOs;

namespace IsMocStudio.Core.Interfaces.Services
{
    /// <summary>
    /// Dossier de conception livré à l'équipe de développement : App_Data/Deliverables/{projectId}/
    /// (design-valide.html + cahier-des-charges.json).
    /// </summary>
    public interface IProjectDeliverableService
    {
        Task<DateTimeOffset> WriteAsync(ProjectMemoryDto memory, CancellationToken cancellationToken = default);
        bool Exists(string projectId);
        Task<(string DesignHtml, string SpecificationJson, DateTimeOffset GeneratedAt)?> ReadAsync(string projectId, CancellationToken cancellationToken = default);
        Task<byte[]?> ZipAsync(string projectId, CancellationToken cancellationToken = default);
    }
}
