using IsMocStudio.Core.Entities;
using IsMocStudio.Core.Interfaces.Repositories;
using IsMocStudio.Core.Interfaces.Services;

namespace IsMocStudio.Business.Services
{
    /// <summary>
    /// Rôle d'un compte = « Admin » si et seulement si son e-mail figure dans « AdminEmails », sinon « Client ».
    /// Appliqué à toute la base au démarrage, puis à chaque connexion (e-mail, Google…) avant d'émettre le JWT.
    /// Jamais de promotion par nom affiché, par « premier inscrit », ni parce qu'un compte était déjà Admin en base.
    /// </summary>
    public static class AdminBootstrapper
    {
        public const string Role = "Admin";
        public const string ClientRole = "Client";

        /// <summary>Rôle attendu pour cet e-mail d'après la liste des administrateurs.</summary>
        public static string ExpectedRole(IAdminAccessRegistry registry, string? email) =>
            registry.IsAdminEmail(email) ? Role : ClientRole;

        /// <summary>Aligne le rôle du compte sur la liste ; retourne true s'il a changé (à enregistrer).</summary>
        public static bool SyncRole(User user, IAdminAccessRegistry registry)
        {
            var expected = ExpectedRole(registry, user.Email);
            if (string.Equals(user.Role, expected, StringComparison.Ordinal)) return false;
            user.Role = expected;
            return true;
        }

        /// <summary>Au démarrage : promeut les comptes de la liste, rétrograde en Client les Admin qui n'y sont pas.</summary>
        public static async Task<RoleSyncResult> SyncRolesAsync(IUnitOfWork unitOfWork, IAdminAccessRegistry registry)
        {
            var promoted = new List<User>();
            var demoted = new List<User>();

            foreach (var user in await unitOfWork.Users.GetAllAsync())
            {
                var wasAdmin = IsAdmin(user);
                if (!SyncRole(user, registry)) continue;
                unitOfWork.Users.Update(user);
                (wasAdmin ? demoted : promoted).Add(user);
            }

            if (promoted.Count > 0 || demoted.Count > 0) await unitOfWork.CompleteAsync();
            return new RoleSyncResult(promoted, demoted);
        }

        public static bool IsAdmin(User user) => string.Equals(user.Role, Role, StringComparison.OrdinalIgnoreCase);
    }

    public sealed record RoleSyncResult(IReadOnlyList<User> Promoted, IReadOnlyList<User> Demoted);
}
