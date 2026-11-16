using IsMocStudio.Core.DTOs;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace IsMocStudio.Core.Interfaces.Services
{
    public interface IAuthService
    {
        Task<AuthResponseDto> RegisterAsync(RegisterRequestDto request);
        Task<AuthResponseDto> LoginAsync(LoginRequestDto request);
        Task<AuthResponseDto> GoogleLoginAsync(string email, string firstName, string lastName);

        // 3. Réinitialisation du mot de passe
        Task ForgotPasswordAsync(string email);

        // 1. Demande l'envoi d'un code SMS
        Task SendSmsOtpAsync(string phoneNumber);

        // 2. Vérifie le code SMS et connecte l'utilisateur
        Task<AuthResponseDto> VerifySmsOtpAsync(string phoneNumber, string code);

        // 4. Compte connecté (onglet Paramètres)
        Task<AccountProfileDto> GetProfileAsync(int userId);

        /// <summary>Met à jour le nom / téléphone et renvoie un nouveau jeton (le nom figure dans les claims).</summary>
        Task<AuthResponseDto> UpdateProfileAsync(int userId, UpdateProfileRequestDto request);

        Task ChangePasswordAsync(int userId, ChangePasswordRequestDto request);
    }
}