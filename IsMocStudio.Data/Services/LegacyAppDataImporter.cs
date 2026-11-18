using System.Text.Json;
using IsMocStudio.Core.DTOs;
using IsMocStudio.Core.Entities;
using IsMocStudio.Data.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace IsMocStudio.Data.Services
{
    /// <summary>
    /// Import unique (au démarrage) des anciennes données fichier App_Data vers PostgreSQL :
    /// Memories/*.json → ProjectMemories, Deliverables/{projectId}/ → ProjectDeliverables, ai-quotas.json → AiQuotaUsages.
    /// Sans doublon (ce qui existe déjà en base est conservé) ; chaque fichier importé ou déjà présent est supprimé
    /// du disque, un fichier illisible est laissé en place et signalé. Sans dossier App_Data, ne fait rien.
    /// </summary>
    public static class LegacyAppDataImporter
    {
        public sealed record Result(int Memories, int Deliverables, int Quotas, int Skipped, int Failed);

        private sealed class LegacyUsage
        {
            public int GenerationsUsed { get; set; }
            public int RefinementsUsed { get; set; }
            public DateTimeOffset UpdatedAt { get; set; }
        }

        public static async Task<Result> ImportAsync(IDbContextFactory<IsMocDbContext> contextFactory, string appDataDirectory, ILogger logger, CancellationToken cancellationToken = default)
        {
            int memories = 0, deliverables = 0, quotas = 0, skipped = 0, failed = 0;
            await using var db = await contextFactory.CreateDbContextAsync(cancellationToken);

            // 1. Discussions / projets
            var memoryDirectory = Path.Combine(appDataDirectory, "Memories");
            if (Directory.Exists(memoryDirectory))
            {
                foreach (var path in Directory.EnumerateFiles(memoryDirectory, "*.json"))
                {
                    var projectId = Path.GetFileNameWithoutExtension(path);
                    if (!Guid.TryParseExact(projectId, "N", out _)) continue;
                    try
                    {
                        if (await db.ProjectMemories.AnyAsync(record => record.ProjectId == projectId, cancellationToken))
                        {
                            skipped++;
                        }
                        else
                        {
                            var memory = ProjectMemoryJson.Deserialize(await File.ReadAllTextAsync(path, cancellationToken))
                                ?? throw new JsonException("Document vide.");
                            memory.ProjectId = projectId;
                            db.ProjectMemories.Add(EfProjectMemoryService.ToRecord(memory));
                            await db.SaveChangesAsync(cancellationToken);
                            memories++;
                        }

                        File.Delete(path);
                    }
                    catch (Exception exception) when (exception is JsonException or IOException or DbUpdateException)
                    {
                        failed++;
                        db.ChangeTracker.Clear();
                        logger.LogWarning(exception, "Import de la mémoire {ProjectId} impossible : fichier conservé.", projectId);
                    }
                }

                DeleteIfEmpty(memoryDirectory);
            }

            // 2. Dossiers de conception
            var deliverableDirectory = Path.Combine(appDataDirectory, "Deliverables");
            if (Directory.Exists(deliverableDirectory))
            {
                foreach (var directory in Directory.EnumerateDirectories(deliverableDirectory))
                {
                    var projectId = Path.GetFileName(directory);
                    if (!Guid.TryParseExact(projectId, "N", out _)) continue;
                    var designPath = Path.Combine(directory, EfProjectDeliverableService.DesignFileName);
                    var specificationPath = Path.Combine(directory, EfProjectDeliverableService.SpecificationFileName);
                    try
                    {
                        if (await db.ProjectDeliverables.AnyAsync(record => record.ProjectId == projectId, cancellationToken))
                        {
                            skipped++;
                        }
                        else if (File.Exists(designPath) && File.Exists(specificationPath))
                        {
                            db.ProjectDeliverables.Add(new ProjectDeliverableRecord
                            {
                                ProjectId = projectId,
                                DesignHtml = await File.ReadAllTextAsync(designPath, cancellationToken),
                                SpecificationJson = await File.ReadAllTextAsync(specificationPath, cancellationToken),
                                GeneratedAt = new DateTimeOffset(File.GetLastWriteTimeUtc(specificationPath), TimeSpan.Zero)
                            });
                            await db.SaveChangesAsync(cancellationToken);
                            deliverables++;
                        }
                        else
                        {
                            // Dossier incomplet : rien d'exploitable, laissé en place.
                            failed++;
                            logger.LogWarning("Dossier de conception incomplet pour {ProjectId} : conservé sur le disque.", projectId);
                            continue;
                        }

                        Directory.Delete(directory, recursive: true);
                    }
                    catch (Exception exception) when (exception is IOException or DbUpdateException)
                    {
                        failed++;
                        db.ChangeTracker.Clear();
                        logger.LogWarning(exception, "Import du dossier de conception {ProjectId} impossible : conservé.", projectId);
                    }
                }

                DeleteIfEmpty(deliverableDirectory);
            }

            // 3. Crédits IA
            var quotaPath = Path.Combine(appDataDirectory, "ai-quotas.json");
            if (File.Exists(quotaPath))
            {
                try
                {
                    var usages = JsonSerializer.Deserialize<Dictionary<string, LegacyUsage>>(
                        await File.ReadAllTextAsync(quotaPath, cancellationToken), new JsonSerializerOptions(JsonSerializerDefaults.Web)) ?? [];
                    foreach (var (userId, usage) in usages)
                    {
                        if (string.IsNullOrWhiteSpace(userId) || await db.AiQuotaUsages.AnyAsync(item => item.UserId == userId, cancellationToken))
                        {
                            skipped++;
                            continue;
                        }

                        db.AiQuotaUsages.Add(new AiQuotaUsage
                        {
                            UserId = userId,
                            GenerationsUsed = Math.Max(0, usage.GenerationsUsed),
                            RefinementsUsed = Math.Max(0, usage.RefinementsUsed),
                            UpdatedAt = usage.UpdatedAt == default ? DateTimeOffset.UtcNow : usage.UpdatedAt
                        });
                        quotas++;
                    }

                    await db.SaveChangesAsync(cancellationToken);
                    File.Delete(quotaPath);
                }
                catch (Exception exception) when (exception is JsonException or IOException or DbUpdateException)
                {
                    failed++;
                    logger.LogWarning(exception, "Import des crédits IA impossible : ai-quotas.json conservé.");
                }
            }

            return new Result(memories, deliverables, quotas, skipped, failed);
        }

        private static void DeleteIfEmpty(string directory)
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(directory).Any()) Directory.Delete(directory);
            }
            catch (IOException)
            {
                // Sans importance : le dossier sera retenté au prochain démarrage.
            }
        }
    }
}
