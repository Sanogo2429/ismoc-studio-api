using IsMocStudio.Core.DTOs;
using IsMocStudio.Core.Entities;
using IsMocStudio.Core.Interfaces.Services;
using IsMocStudio.Data.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace IsMocStudio.Data.Services
{
    /// <summary>
    /// Mémoires de projet dans PostgreSQL (table ProjectMemories, document jsonb).
    /// Singleton (utilisé par le hub SignalR) : un DbContext court par opération via IDbContextFactory.
    /// Les modifications concurrentes (messagerie) sont sérialisées par un verrou de ligne (SELECT … FOR UPDATE),
    /// valable même avec plusieurs instances de l'API.
    /// </summary>
    public sealed class EfProjectMemoryService : IProjectMemoryService
    {
        private readonly IDbContextFactory<IsMocDbContext> _contextFactory;
        private readonly ILogger<EfProjectMemoryService> _logger;

        public EfProjectMemoryService(IDbContextFactory<IsMocDbContext> contextFactory, ILogger<EfProjectMemoryService> logger)
        {
            _contextFactory = contextFactory;
            _logger = logger;
        }

        public string NewProjectId() => Guid.NewGuid().ToString("N");

        /// <summary>
        /// GUID déterministe dérivé de « general-{ownerId} » : un seul fil par client, retrouvable sans index,
        /// et compatible avec toutes les validations d'identifiant (mémoire, pièces jointes, hub SignalR).
        /// </summary>
        public string GeneralThreadId(string ownerId) =>
            new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes($"general-{ownerId}"))).ToString("N");

        public async Task<ProjectMemoryDto> EnsureGeneralThreadAsync(string ownerId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("Propriétaire invalide.", nameof(ownerId));
            var projectId = GeneralThreadId(ownerId);
            var existing = await GetAsync(projectId, cancellationToken);
            if (existing is not null) return existing;

            var thread = new ProjectMemoryDto
            {
                ProjectId = projectId,
                OwnerId = ownerId,
                CreationMode = ProjectThreadTypes.General,
                ThreadType = ProjectThreadTypes.General,
                ProjectName = ProjectThreadTypes.GeneralName,
                Status = ProjectStatuses.Draft,
                ClientMessages =
                [
                    new ClientMessageDto
                    {
                        Author = "team",
                        AuthorName = "Équipe IsMoc",
                        Content = "Bonjour et bienvenue chez IsMoc Studio ! Posez-nous toutes vos questions avant de lancer votre projet : nous répondons ici, et vous pouvez aussi nous appeler en audio ou en vidéo."
                    }
                ]
            };

            try
            {
                await CreateAsync(thread, cancellationToken);
                return thread;
            }
            catch (IOException)
            {
                // Créé entre-temps par une requête concurrente.
                return await GetAsync(projectId, cancellationToken) ?? thread;
            }
        }

        public async Task CreateAsync(ProjectMemoryDto memory, CancellationToken cancellationToken = default)
        {
            ValidateProjectId(memory.ProjectId);
            memory.CreatedAt = DateTimeOffset.UtcNow;
            memory.UpdatedAt = memory.CreatedAt;

            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            db.ProjectMemories.Add(ToRecord(memory));
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Même contrat que l'ancien stockage fichier : identifiant déjà utilisé.
                throw new IOException("Une mémoire existe déjà pour cet identifiant de projet.", exception);
            }
        }

        public async Task<ProjectMemoryDto?> GetAsync(string projectId, CancellationToken cancellationToken = default)
        {
            ValidateProjectId(projectId);
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var document = await db.ProjectMemories.AsNoTracking()
                .Where(record => record.ProjectId == projectId)
                .Select(record => record.Document)
                .SingleOrDefaultAsync(cancellationToken);
            return document is null ? null : ProjectMemoryJson.Deserialize(document);
        }

        public async Task SaveAsync(ProjectMemoryDto memory, CancellationToken cancellationToken = default)
        {
            ValidateProjectId(memory.ProjectId);
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var record = await LockAsync(db, memory.ProjectId, cancellationToken)
                ?? throw new FileNotFoundException("La mémoire du projet est introuvable.");

            // Les messages client sont en ajout seul : on conserve ceux reçus (SignalR) pendant qu'une
            // opération longue (ex. appel IA) travaillait sur une copie plus ancienne de la mémoire.
            var stored = ProjectMemoryJson.Deserialize(record.Document);
            if (stored is not null)
            {
                var knownIds = memory.ClientMessages.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
                var missing = stored.ClientMessages.Where(message => !knownIds.Contains(message.Id)).ToList();
                if (missing.Count > 0)
                {
                    memory.ClientMessages.AddRange(missing);
                    memory.ClientMessages.Sort((left, right) => left.SentAt.CompareTo(right.SentAt));
                }
            }

            memory.UpdatedAt = DateTimeOffset.UtcNow;
            Apply(record, memory);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }

        public async Task<ProjectMemoryDto?> UpdateAsync(string projectId, Action<ProjectMemoryDto> mutate, CancellationToken cancellationToken = default)
        {
            ValidateProjectId(projectId);
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
            var record = await LockAsync(db, projectId, cancellationToken);
            if (record is null) return null;

            var memory = ProjectMemoryJson.Deserialize(record.Document);
            if (memory is null) return null;
            mutate(memory);
            memory.UpdatedAt = DateTimeOffset.UtcNow;
            Apply(record, memory);
            await db.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return memory;
        }

        public Task<IReadOnlyList<ProjectMemoryDto>> ListByOwnerAsync(string ownerId, CancellationToken cancellationToken = default) =>
            ListAsync(record => record.OwnerId == ownerId, cancellationToken);

        public Task<IReadOnlyList<ProjectMemoryDto>> ListAllAsync(CancellationToken cancellationToken = default) =>
            ListAsync(_ => true, cancellationToken);

        private async Task<IReadOnlyList<ProjectMemoryDto>> ListAsync(System.Linq.Expressions.Expression<Func<ProjectMemoryRecord, bool>> predicate, CancellationToken cancellationToken)
        {
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var rows = await db.ProjectMemories.AsNoTracking()
                .Where(predicate)
                .OrderByDescending(record => record.UpdatedAt)
                .Select(record => new { record.ProjectId, record.Document })
                .ToListAsync(cancellationToken);

            var result = new List<ProjectMemoryDto>(rows.Count);
            foreach (var row in rows)
            {
                try
                {
                    if (ProjectMemoryJson.Deserialize(row.Document) is { } memory) result.Add(memory);
                }
                catch (System.Text.Json.JsonException exception)
                {
                    // Un document illisible ne doit pas empêcher l'affichage des autres projets.
                    _logger.LogWarning(exception, "Mémoire illisible ignorée : {ProjectId}.", row.ProjectId);
                }
            }

            return result;
        }

        /// <summary>Lit la ligne en la verrouillant jusqu'à la fin de la transaction (les autres écritures attendent).</summary>
        private static async Task<ProjectMemoryRecord?> LockAsync(IsMocDbContext db, string projectId, CancellationToken cancellationToken)
        {
            var rows = await db.ProjectMemories
                .FromSqlInterpolated($"SELECT * FROM \"ProjectMemories\" WHERE \"ProjectId\" = {projectId} FOR UPDATE")
                .ToListAsync(cancellationToken);
            return rows.SingleOrDefault();
        }

        public static ProjectMemoryRecord ToRecord(ProjectMemoryDto memory)
        {
            var record = new ProjectMemoryRecord { ProjectId = memory.ProjectId };
            Apply(record, memory);
            return record;
        }

        private static void Apply(ProjectMemoryRecord record, ProjectMemoryDto memory)
        {
            record.OwnerId = memory.OwnerId ?? string.Empty;
            record.ThreadType = memory.ThreadType ?? ProjectThreadTypes.Project;
            record.Status = memory.Status ?? ProjectStatuses.Draft;
            record.ProjectName = Truncate(memory.ProjectName, 200);
            record.CreatedAt = memory.CreatedAt;
            record.UpdatedAt = memory.UpdatedAt;
            record.Document = ProjectMemoryJson.Serialize(memory);
        }

        private static string Truncate(string? value, int maxLength) =>
            string.IsNullOrEmpty(value) ? string.Empty : value.Length > maxLength ? value[..maxLength] : value;

        private static void ValidateProjectId(string projectId)
        {
            if (!Guid.TryParseExact(projectId, "N", out _))
            {
                throw new ArgumentException("L’identifiant du projet est invalide.", nameof(projectId));
            }
        }
    }
}
