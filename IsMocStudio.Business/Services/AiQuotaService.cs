using System.Collections.Concurrent;
using System.Text.Json;
using IsMocStudio.Core.Interfaces.Services;
using Microsoft.Extensions.Configuration;

namespace IsMocStudio.Business.Services
{
    /// <summary>
    /// Protège les tokens OpenAI :
    /// - crédits gratuits par compte (« AiQuotas:FreeGenerations », « AiQuotas:FreeRefinements »), persistés dans App_Data/ai-quotas.json ;
    /// - illimité pour les Admin et dès qu'un projet est payé (décidé par l'appelant) ;
    /// - limite anti-spam glissante (« AiQuotas:MaxRequestsPerMinute ») en mémoire.
    /// </summary>
    public sealed class AiQuotaService : IAiQuotaService
    {
        private sealed class Usage
        {
            public int GenerationsUsed { get; set; }
            public int RefinementsUsed { get; set; }
            public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
        }

        private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web) { WriteIndented = true };
        private static readonly TimeSpan RateWindow = TimeSpan.FromMinutes(1);

        private readonly string _path;
        private readonly int _freeGenerations;
        private readonly int _freeRefinements;
        private readonly int _maxRequestsPerMinute;
        private readonly Dictionary<string, Usage> _usage;
        private readonly SemaphoreSlim _lock = new(1, 1);
        private readonly ConcurrentDictionary<string, Queue<DateTimeOffset>> _recentRequests = new(StringComparer.Ordinal);

        public AiQuotaService(IConfiguration configuration, string path)
        {
            _path = path;
            _freeGenerations = ReadInt(configuration, "AiQuotas:FreeGenerations", 3);
            _freeRefinements = ReadInt(configuration, "AiQuotas:FreeRefinements", 6);
            _maxRequestsPerMinute = ReadInt(configuration, "AiQuotas:MaxRequestsPerMinute", 5);
            _usage = Load(path);
        }

        public AiQuotaStatus GetStatus(string userId, bool unlimited)
        {
            _lock.Wait();
            try
            {
                var usage = _usage.GetValueOrDefault(userId) ?? new Usage();
                return new AiQuotaStatus(
                    unlimited,
                    _freeGenerations,
                    _freeRefinements,
                    unlimited ? _freeGenerations : Math.Max(0, _freeGenerations - usage.GenerationsUsed),
                    unlimited ? _freeRefinements : Math.Max(0, _freeRefinements - usage.RefinementsUsed),
                    _maxRequestsPerMinute);
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task<AiQuotaDecision> TryConsumeAsync(string userId, AiUsageKind kind, bool unlimited, bool applyRateLimit, CancellationToken cancellationToken = default)
        {
            if (applyRateLimit && !RegisterRequest(userId)) return AiQuotaDecision.RateLimited;
            if (unlimited || kind == AiUsageKind.Untracked) return AiQuotaDecision.Allowed;

            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (!_usage.TryGetValue(userId, out var usage)) _usage[userId] = usage = new Usage();
                if (kind == AiUsageKind.Generation)
                {
                    if (usage.GenerationsUsed >= _freeGenerations) return AiQuotaDecision.QuotaExceeded;
                    usage.GenerationsUsed++;
                }
                else
                {
                    if (usage.RefinementsUsed >= _freeRefinements) return AiQuotaDecision.QuotaExceeded;
                    usage.RefinementsUsed++;
                }

                usage.UpdatedAt = DateTimeOffset.UtcNow;
                await SaveAsync(cancellationToken);
                return AiQuotaDecision.Allowed;
            }
            finally
            {
                _lock.Release();
            }
        }

        public async Task RefundAsync(string userId, AiUsageKind kind, CancellationToken cancellationToken = default)
        {
            if (kind == AiUsageKind.Untracked) return;
            await _lock.WaitAsync(cancellationToken);
            try
            {
                if (!_usage.TryGetValue(userId, out var usage)) return;
                if (kind == AiUsageKind.Generation && usage.GenerationsUsed > 0) usage.GenerationsUsed--;
                if (kind == AiUsageKind.Refinement && usage.RefinementsUsed > 0) usage.RefinementsUsed--;
                usage.UpdatedAt = DateTimeOffset.UtcNow;
                await SaveAsync(cancellationToken);
            }
            finally
            {
                _lock.Release();
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

        private async Task SaveAsync(CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
            await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(_usage, SerializerOptions), cancellationToken);
            File.Move(temporaryPath, _path, true);
        }

        private static Dictionary<string, Usage> Load(string path)
        {
            if (!File.Exists(path)) return new Dictionary<string, Usage>(StringComparer.Ordinal);
            try
            {
                return JsonSerializer.Deserialize<Dictionary<string, Usage>>(File.ReadAllText(path), SerializerOptions) is { } loaded
                    ? new Dictionary<string, Usage>(loaded, StringComparer.Ordinal)
                    : new Dictionary<string, Usage>(StringComparer.Ordinal);
            }
            catch (JsonException)
            {
                return new Dictionary<string, Usage>(StringComparer.Ordinal);
            }
        }

        private static int ReadInt(IConfiguration configuration, string key, int fallback) =>
            int.TryParse(configuration[key], out var value) && value >= 0 ? value : fallback;
    }
}
