using System.Collections.Concurrent;
using IsMocStudio.Core.Entities;
using IsMocStudio.Core.Interfaces.Services;
using IsMocStudio.Data.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Npgsql;

namespace IsMocStudio.Data.Services
{
    /// <summary>
    /// Protège les tokens IA :
    /// - crédits gratuits par compte (« AiQuotas:FreeGenerations », « AiQuotas:FreeRefinements »), en base (table AiQuotaUsages) ;
    /// - illimité pour les Admin et dès qu'un projet est payé (décidé par l'appelant) ;
    /// - limite anti-spam glissante (« AiQuotas:MaxRequestsPerMinute ») en mémoire.
    /// Le décompte est atomique côté base (UPDATE … WHERE utilisé &lt; limite) : jamais de crédit consommé deux fois.
    /// </summary>
    public sealed class EfAiQuotaService : IAiQuotaService
    {
        private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);

        private readonly IDbContextFactory<IsMocDbContext> _contextFactory;
        private readonly int _freeGenerations;
        private readonly int _freeRefinements;
        private readonly int _maxRequestsPerMinute;
        private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _recentRequests = new(StringComparer.Ordinal);

        public EfAiQuotaService(IDbContextFactory<IsMocDbContext> contextFactory, IConfiguration configuration)
        {
            _contextFactory = contextFactory;
            _freeGenerations = ReadInt(configuration, "AiQuotas:FreeGenerations", 3);
            _freeRefinements = ReadInt(configuration, "AiQuotas:FreeRefinements", 6);
            _maxRequestsPerMinute = ReadInt(configuration, "AiQuotas:MaxRequestsPerMinute", 5);
        }

        public async Task<AiQuotaStatus> GetStatusAsync(string userId, bool unlimited, CancellationToken cancellationToken = default)
        {
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var usage = await db.AiQuotaUsages.AsNoTracking().SingleOrDefaultAsync(item => item.UserId == userId, cancellationToken);
            return new AiQuotaStatus(
                unlimited,
                _freeGenerations,
                _freeRefinements,
                unlimited ? _freeGenerations : Math.Max(0, _freeGenerations - (usage?.GenerationsUsed ?? 0)),
                unlimited ? _freeRefinements : Math.Max(0, _freeRefinements - (usage?.RefinementsUsed ?? 0)),
                _maxRequestsPerMinute);
        }

        public async Task<AiQuotaDecision> TryConsumeAsync(string userId, AiUsageKind kind, bool unlimited, bool applyRateLimit, CancellationToken cancellationToken = default)
        {
            if (applyRateLimit && !RegisterRequest(userId)) return AiQuotaDecision.RateLimited;
            if (unlimited || kind == AiUsageKind.Untracked) return AiQuotaDecision.Allowed;

            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            await EnsureRowAsync(db, userId, cancellationToken);

            var now = DateTimeOffset.UtcNow;
            var consumed = kind == AiUsageKind.Generation
                ? await db.AiQuotaUsages
                    .Where(item => item.UserId == userId && item.GenerationsUsed < _freeGenerations)
                    .ExecuteUpdateAsync(set => set.SetProperty(item => item.GenerationsUsed, item => item.GenerationsUsed + 1).SetProperty(item => item.UpdatedAt, now), cancellationToken)
                : await db.AiQuotaUsages
                    .Where(item => item.UserId == userId && item.RefinementsUsed < _freeRefinements)
                    .ExecuteUpdateAsync(set => set.SetProperty(item => item.RefinementsUsed, item => item.RefinementsUsed + 1).SetProperty(item => item.UpdatedAt, now), cancellationToken);

            return consumed == 1 ? AiQuotaDecision.Allowed : AiQuotaDecision.QuotaExceeded;
        }

        public async Task RefundAsync(string userId, AiUsageKind kind, CancellationToken cancellationToken = default)
        {
            if (kind == AiUsageKind.Untracked) return;
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            var now = DateTimeOffset.UtcNow;
            if (kind == AiUsageKind.Generation)
            {
                await db.AiQuotaUsages
                    .Where(item => item.UserId == userId && item.GenerationsUsed > 0)
                    .ExecuteUpdateAsync(set => set.SetProperty(item => item.GenerationsUsed, item => item.GenerationsUsed - 1).SetProperty(item => item.UpdatedAt, now), cancellationToken);
            }
            else
            {
                await db.AiQuotaUsages
                    .Where(item => item.UserId == userId && item.RefinementsUsed > 0)
                    .ExecuteUpdateAsync(set => set.SetProperty(item => item.RefinementsUsed, item => item.RefinementsUsed - 1).SetProperty(item => item.UpdatedAt, now), cancellationToken);
            }
        }

        /// <summary>Crée la ligne du compte si besoin (une création concurrente est sans effet).</summary>
        private static async Task EnsureRowAsync(IsMocDbContext db, string userId, CancellationToken cancellationToken)
        {
            if (await db.AiQuotaUsages.AnyAsync(item => item.UserId == userId, cancellationToken)) return;
            db.AiQuotaUsages.Add(new AiQuotaUsage { UserId = userId, UpdatedAt = DateTimeOffset.UtcNow });
            try
            {
                await db.SaveChangesAsync(cancellationToken);
            }
            catch (DbUpdateException exception) when (exception.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                // Créée entre-temps par une requête concurrente.
            }
            finally
            {
                db.ChangeTracker.Clear();
            }
        }

        /// <summary>Fenêtre glissante d'une minute ; false si la limite est atteinte.</summary>
        private bool RegisterRequest(string userId)
        {
            var queue = _recentRequests.GetOrAdd(userId, _ => new Queue<DateTimeOffset>());
            lock (queue)
            {
                var now = DateTimeOffset.UtcNow;
                while (queue.Count > 0 && now - queue.Peek() > RateWindow) queue.Dequeue();
                if (queue.Count >= _maxRequestsPerMinute) return false;
                queue.Enqueue(now);
                return true;
            }
        }

        private static int ReadInt(IConfiguration configuration, string key, int fallback) =>
            int.TryParse(configuration[key], out var value) && value >= 0 ? value : fallback;
    }
}
