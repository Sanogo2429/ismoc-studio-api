using IsMocStudio.Core.Entities;
using IsMocStudio.Core.Interfaces.Repositories;
using IsMocStudio.Core.Interfaces.Services;

namespace IsMocStudio.Business.Services
{
    /// <summary>
    /// Promotion automatique au démarrage : aucun accès manuel à la base n'est nécessaire.
    /// 1. Tout compte EXISTANT dont l'e-mail figure dans AdminEmails passe en rôle « Admin ».
    /// 2. L'e-mail de chaque Admin est ajouté à la liste des administrateurs autorisés.
    /// Sécurité : plus aucune promotion par nom affiché (modifiable par n'importe quel client dans « Paramètres »)
    /// ni du « premier inscrit » (sur une base neuve, le premier visiteur serait devenu administrateur).
    /// </summary>
    public static class AdminBootstrapper
    {
        public const string Role = "Admin";

        public static async Task<IReadOnlyList<User>> PromoteAsync(IUnitOfWork unitOfWork, IAdminAccessRegistry registry, CancellationToken cancellationToken = default)
        {
            var users = (await unitOfWork.Users.GetAllAsync()).OrderBy(user => user.Id).ToList();
            if (users.Count == 0) return [];

            var promoted = new List<User>();
            foreach (var user in users.Where(user => !IsAdmin(user) && registry.IsAdminEmail(user.Email)))
            {
                user.Role = Role;
                unitOfWork.Users.Update(user);
                promoted.Add(user);
            }

            if (promoted.Count > 0) await unitOfWork.CompleteAsync();

            // La liste des administrateurs autorisés reflète tous les comptes Admin.
            foreach (var admin in users.Where(IsAdmin)) await registry.AddAsync(admin.Email, cancellationToken);
            return promoted;
        }

        public static bool IsAdmin(User user) => string.Equals(user.Role, Role, StringComparison.OrdinalIgnoreCase);
    }
}
