using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IsMocStudio.Business.Helpers
{
    public static class PasswordHelper
    {
        /// <summary>
        /// « Mot de passe » d'un compte créé avec Google : volontairement inutilisable (pas un hachage BCrypt). Le compte
        /// n'a pas de mot de passe IsMoc tant que l'utilisateur n'en définit pas un (« Mot de passe oublié »).
        /// </summary>
        public const string ExternalAccountMarker = "!external:google";

        public static string HashPassword(string password)
        {
            return BCrypt.Net.BCrypt.HashPassword(password);
        }

        /// <summary>Le compte possède un vrai mot de passe IsMoc (hachage BCrypt), et non le marqueur d'un compte Google.</summary>
        public static bool HasUsablePassword(string? hashedPassword) =>
            !string.IsNullOrEmpty(hashedPassword) && hashedPassword.StartsWith("$2", StringComparison.Ordinal);

        /// <summary>Faux (jamais d'exception) si le compte n'a pas de mot de passe utilisable.</summary>
        public static bool VerifyPassword(string password, string hashedPassword)
        {
            if (!HasUsablePassword(hashedPassword)) return false;
            try
            {
                return BCrypt.Net.BCrypt.Verify(password, hashedPassword);
            }
            catch (BCrypt.Net.SaltParseException)
            {
                return false;
            }
        }
    }
}
