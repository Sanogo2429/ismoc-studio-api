namespace IsMocStudio.api.Security
{
    /// <summary>Noms des politiques de limitation de débit (configurées dans Program.cs).</summary>
    public static class RateLimitPolicies
    {
        /// <summary>Connexion, inscription, SMS : 10 requêtes / minute / IP.</summary>
        public const string Auth = "auth";

        /// <summary>Mot de passe oublié : 5 requêtes / 15 minutes / IP.</summary>
        public const string PasswordReset = "password-reset";
    }
}
