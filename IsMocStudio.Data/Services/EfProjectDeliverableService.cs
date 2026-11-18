using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using IsMocStudio.Core.DTOs;
using IsMocStudio.Core.Entities;
using IsMocStudio.Core.Interfaces.Services;
using IsMocStudio.Data.Context;
using Microsoft.EntityFrameworkCore;

namespace IsMocStudio.Data.Services
{
    /// <summary>
    /// Dossier prêt pour l'équipe de développement, en base (table ProjectDeliverables) :
    /// design-valide.html (maquette validée) et cahier-des-charges.json (brief, préférences, devis €, écrans, historique).
    /// </summary>
    public sealed class EfProjectDeliverableService : IProjectDeliverableService
    {
        public const string DesignFileName = "design-valide.html";
        public const string SpecificationFileName = "cahier-des-charges.json";

        private static readonly UTF8Encoding Utf8NoBom = new(false);
        private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };

        private readonly IDbContextFactory<IsMocDbContext> _contextFactory;

        public EfProjectDeliverableService(IDbContextFactory<IsMocDbContext> contextFactory) => _contextFactory = contextFactory;

        public async Task<DateTimeOffset> WriteAsync(ProjectMemoryDto memory, CancellationToken cancellationToken = default)
        {
            ValidateProjectId(memory.ProjectId);
            var generatedAt = DateTimeOffset.UtcNow;

            var specification = new
            {
                documentType = "Cahier des charges IsMoc Studio",
                generatedAt,
                project = new
                {
                    memory.ProjectId,
                    memory.ProjectName,
                    type = memory.Format == "mobile" ? "Application mobile" : "Site web",
                    memory.Format,
                    memory.CreationMode,
                    memory.Status,
                    memory.CreatedAt
                },
                memory.InitialIdea,
                memory.UserPreferences,
                screens = memory.Quote?.Screens ?? [],
                features = memory.Quote?.Features ?? [],
                quoteEur = memory.Quote,
                order = memory.Order is null ? null : new
                {
                    memory.Order.InvoiceNumber,
                    memory.Order.MaintenancePlan,
                    memory.Order.MaintenanceAmountEur,
                    memory.Order.MaintenancePeriod,
                    memory.Order.DevelopmentPriceEur,
                    memory.Order.DesignIdentityPriceEur,
                    memory.Order.ProjectTotalEur,
                    memory.Order.Installments,
                    memory.Order.PaymentMethod,
                    memory.Order.OrderedAt,
                    memory.Order.PaidAt
                },
                memory.Milestones,
                designFile = DesignFileName,
                chatHistory = memory.ChatHistory,
                clientMessages = memory.ClientMessages
            };

            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var record = await db.ProjectDeliverables.FindAsync([memory.ProjectId], cancellationToken);
            if (record is null) db.ProjectDeliverables.Add(record = new ProjectDeliverableRecord { ProjectId = memory.ProjectId });
            record.DesignHtml = memory.CurrentHtml ?? string.Empty;
            record.SpecificationJson = JsonSerializer.Serialize(specification, SerializerOptions);
            record.GeneratedAt = generatedAt;
            await db.SaveChangesAsync(cancellationToken);
            return generatedAt;
        }

        public async Task<bool> ExistsAsync(string projectId, CancellationToken cancellationToken = default)
        {
            ValidateProjectId(projectId);
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            return await db.ProjectDeliverables.AnyAsync(record => record.ProjectId == projectId, cancellationToken);
        }

        public async Task<IReadOnlySet<string>> ProjectIdsWithDeliverableAsync(CancellationToken cancellationToken = default)
        {
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var ids = await db.ProjectDeliverables.Select(record => record.ProjectId).ToListAsync(cancellationToken);
            return ids.ToHashSet(StringComparer.Ordinal);
        }

        public async Task<(string DesignHtml, string SpecificationJson, DateTimeOffset GeneratedAt)?> ReadAsync(string projectId, CancellationToken cancellationToken = default)
        {
            ValidateProjectId(projectId);
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var record = await db.ProjectDeliverables.AsNoTracking().SingleOrDefaultAsync(item => item.ProjectId == projectId, cancellationToken);
            return record is null ? null : (record.DesignHtml, record.SpecificationJson, record.GeneratedAt);
        }

        public async Task<byte[]?> ZipAsync(string projectId, CancellationToken cancellationToken = default)
        {
            var deliverable = await ReadAsync(projectId, cancellationToken);
            if (deliverable is null) return null;

            using var buffer = new MemoryStream();
            using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, true))
            {
                await AddEntryAsync(archive, DesignFileName, deliverable.Value.DesignHtml, cancellationToken);
                await AddEntryAsync(archive, SpecificationFileName, deliverable.Value.SpecificationJson, cancellationToken);
            }

            return buffer.ToArray();
        }

        private static async Task AddEntryAsync(ZipArchive archive, string name, string content, CancellationToken cancellationToken)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            await using var stream = entry.Open();
            await stream.WriteAsync(Utf8NoBom.GetBytes(content), cancellationToken);
        }

        private static void ValidateProjectId(string projectId)
        {
            if (!Guid.TryParseExact(projectId, "N", out _)) throw new ArgumentException("L’identifiant du projet est invalide.", nameof(projectId));
        }
    }
}
