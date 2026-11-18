using IsMocStudio.Data.Context;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace IsMocStudio.api.Security
{
    /// <summary>
    /// /health interroge réellement PostgreSQL : utile au ping hebdomadaire (GitHub Actions) qui empêche
    /// la mise en pause d'un projet Supabase gratuit après 7 jours sans activité.
    /// </summary>
    public sealed class DatabaseHealthCheck : IHealthCheck
    {
        private readonly IDbContextFactory<IsMocDbContext> _contextFactory;

        public DatabaseHealthCheck(IDbContextFactory<IsMocDbContext> contextFactory) => _contextFactory = contextFactory;

        public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
        {
            try
            {
                await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
                // Vraie requête (pas seulement l'ouverture de connexion) : compte comme activité côté Supabase.
                await db.ProjectMemories.AsNoTracking().Select(memory => memory.ProjectId).FirstOrDefaultAsync(cancellationToken);
                return HealthCheckResult.Healthy();
            }
            catch (Exception exception)
            {
                return HealthCheckResult.Unhealthy("Base de données injoignable.", exception);
            }
        }
    }
}
