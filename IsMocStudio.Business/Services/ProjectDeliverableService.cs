using System.IO.Compression;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using IsMocStudio.Core.DTOs;
using IsMocStudio.Core.Interfaces.Services;

namespace IsMocStudio.Business.Services
{
    /// <summary>
    /// Écrit le dossier prêt pour l'équipe de développement dans App_Data/Deliverables/{projectId}/ :
    /// design-valide.html (maquette validée) et cahier-des-charges.json (brief, préférences, devis €, écrans, historique).
    /// </summary>
    public sealed class ProjectDeliverableService : IProjectDeliverableService
    {
        public const string DesignFileName = "design-valide.html";
        public const string SpecificationFileName = "cahier-des-charges.json";

        private static readonly UTF8Encoding Utf8NoBom = new(false);
        private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };

        private readonly string _rootDirectory;

        public ProjectDeliverableService(string rootDirectory)
        {
            if (string.IsNullOrWhiteSpace(rootDirectory)) throw new ArgumentException("Le dossier des livrables est obligatoire.", nameof(rootDirectory));
            _rootDirectory = rootDirectory;
            Directory.CreateDirectory(_rootDirectory);
        }

        public async Task<DateTimeOffset> WriteAsync(ProjectMemoryDto memory, CancellationToken cancellationToken = default)
        {
            var directory = GetDirectory(memory.ProjectId);
            Directory.CreateDirectory(directory);
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

            await WriteAtomicallyAsync(Path.Combine(directory, DesignFileName), memory.CurrentHtml, cancellationToken);
            await WriteAtomicallyAsync(Path.Combine(directory, SpecificationFileName), JsonSerializer.Serialize(specification, SerializerOptions), cancellationToken);
            return generatedAt;
        }

        public bool Exists(string projectId) => File.Exists(Path.Combine(GetDirectory(projectId), SpecificationFileName));

        public async Task<(string DesignHtml, string SpecificationJson, DateTimeOffset GeneratedAt)?> ReadAsync(string projectId, CancellationToken cancellationToken = default)
        {
            var directory = GetDirectory(projectId);
            var designPath = Path.Combine(directory, DesignFileName);
            var specificationPath = Path.Combine(directory, SpecificationFileName);
            if (!File.Exists(designPath) || !File.Exists(specificationPath)) return null;

            var design = await File.ReadAllTextAsync(designPath, Encoding.UTF8, cancellationToken);
            var specification = await File.ReadAllTextAsync(specificationPath, Encoding.UTF8, cancellationToken);
            return (design, specification, new DateTimeOffset(File.GetLastWriteTimeUtc(specificationPath), TimeSpan.Zero));
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

        private string GetDirectory(string projectId)
        {
            if (!Guid.TryParseExact(projectId, "N", out _)) throw new ArgumentException("L’identifiant du projet est invalide.", nameof(projectId));
            return Path.Combine(_rootDirectory, projectId);
        }

        private static async Task AddEntryAsync(ZipArchive archive, string name, string content, CancellationToken cancellationToken)
        {
            var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
            await using var stream = entry.Open();
            await stream.WriteAsync(Utf8NoBom.GetBytes(content), cancellationToken);
        }

        private static async Task WriteAtomicallyAsync(string path, string content, CancellationToken cancellationToken)
        {
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await File.WriteAllTextAsync(temporaryPath, content, Utf8NoBom, cancellationToken);
                File.Move(temporaryPath, path, true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }
    }
}
