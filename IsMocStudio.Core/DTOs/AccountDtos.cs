namespace IsMocStudio.Core.DTOs
{
    /// <summary>Profil du compte connecté (onglet Profil).</summary>
    public class AccountProfileDto
    {
        public int Id { get; set; }
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string FullName { get; set; } = string.Empty;
        public string Email { get; set; } = string.Empty;
        public string PhoneNumber { get; set; } = string.Empty;
        public string Role { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }

        /// <summary>Le compte a un mot de passe IsMoc (faux pour un compte créé avec Google sans mot de passe défini).</summary>
        public bool HasPassword { get; set; }

        /// <summary>JWT régénéré avec le rôle courant (ex. compte promu Admin depuis la dernière connexion).</summary>
        public string Token { get; set; } = string.Empty;
    }

    public class UpdateProfileRequestDto
    {
        public string FirstName { get; set; } = string.Empty;
        public string LastName { get; set; } = string.Empty;
        public string? PhoneNumber { get; set; }
    }

    public class ChangePasswordRequestDto
    {
        public string CurrentPassword { get; set; } = string.Empty;
        public string NewPassword { get; set; } = string.Empty;
    }
}
