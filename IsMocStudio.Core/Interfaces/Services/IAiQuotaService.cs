namespace IsMocStudio.Core.Interfaces.Services
{
    public enum AiUsageKind
    {
        /// <summary>Génération d'une maquette en Mode IA (décompte du quota gratuit).</summary>
        Generation,

        /// <summary>Retouche IA dans le chat (décompte du quota gratuit).</summary>
        Refinement,

        /// <summary>Appel IA non décompté (Mode Manuel, devis) : seule la limite anti-spam s'applique.</summary>
        Untracked
    }

    public enum AiQuotaDecision
    {
        Allowed,
        QuotaExceeded,
        RateLimited
    }

    public sealed record AiQuotaStatus(bool Unlimited, int GenerationsLimit, int RefinementsLimit, int RemainingGenerations, int RemainingRefinements, int MaxRequestsPerMinute);

    /// <summary>Crédits IA gratuits par utilisateur (App_Data/ai-quotas.json) et limite anti-spam par minute.</summary>
    public interface IAiQuotaService
    {
        AiQuotaStatus GetStatus(string userId, bool unlimited);

        /// <summary>
        /// Vérifie la limite par minute (si <paramref name="applyRateLimit"/>) puis consomme un crédit
        /// (sauf si <paramref name="unlimited"/> ou <see cref="AiUsageKind.Untracked"/>).
        /// </summary>
        Task<AiQuotaDecision> TryConsumeAsync(string userId, AiUsageKind kind, bool unlimited, bool applyRateLimit, CancellationToken cancellationToken = default);

        /// <summary>Restitue un crédit consommé quand l'appel IA a échoué.</summary>
        Task RefundAsync(string userId, AiUsageKind kind, CancellationToken cancellationToken = default);
    }
}
