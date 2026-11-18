using IsMocStudio.Core.DTOs;

namespace IsMocStudio.Core.Interfaces.Services
{
    /// <summary>
    /// Mémoire persistante d'un projet IsMoc : une ligne par projet dans PostgreSQL (table ProjectMemories, document jsonb).
    /// </summary>
    public interface IProjectMemoryService
    {
        string NewProjectId();
        Task CreateAsync(ProjectMemoryDto memory, CancellationToken cancellationToken = default);
        Task<ProjectMemoryDto?> GetAsync(string projectId, CancellationToken cancellationToken = default);
        Task SaveAsync(ProjectMemoryDto memory, CancellationToken cancellationToken = default);

        /// <summary>Lecture → modification → écriture atomique sous verrou (pour les ajouts concurrents, ex. messagerie).</summary>
        Task<ProjectMemoryDto?> UpdateAsync(string projectId, Action<ProjectMemoryDto> mutate, CancellationToken cancellationToken = default);
        Task<IReadOnlyList<ProjectMemoryDto>> ListByOwnerAsync(string ownerId, CancellationToken cancellationToken = default);

        /// <summary>Identifiant (GUID déterministe dérivé de « general-{ownerId} ») du fil de discussion général d'un client.</summary>
        string GeneralThreadId(string ownerId);

        /// <summary>Crée au besoin le fil « Discussion générale · Avant-projet » du client et le retourne.</summary>
        Task<ProjectMemoryDto> EnsureGeneralThreadAsync(string ownerId, CancellationToken cancellationToken = default);

        /// <summary>Tous les projets de la plateforme (back-office agence), du plus récent au plus ancien.</summary>
        Task<IReadOnlyList<ProjectMemoryDto>> ListAllAsync(CancellationToken cancellationToken = default);
    }
}
