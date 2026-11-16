using IsMocStudio.Core.DTOs;
using IsMocStudio.Core.Entities;
using IsMocStudio.Core.Interfaces.Repositories;
using IsMocStudio.Core.Interfaces.Services;
using IsMocStudio.Business.Helpers;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using System;
using System.Net;
using System.Security.Cryptography;
using System.Threading.Tasks;

namespace IsMocStudio.Business.Services
{
    public class AuthService : IAuthService
    {
        private readonly IUnitOfWork _unitOfWork;
        private readonly TokenHelper _tokenHelper;
        private readonly IEmailService _emailService;
        private readonly ISmsService _smsService;
        private readonly IMemoryCache _cache;
        private readonly IAdminAccessRegistry _adminRegistry;
        private readonly ILogger<AuthService> _logger;

        public AuthService(
            IUnitOfWork unitOfWork,
            TokenHelper tokenHelper,
            IEmailService emailService,
            ISmsService smsService,
            IMemoryCache cache,
            IAdminAccessRegistry adminRegistry,
            ILogger<AuthService> logger)
        {
            _unitOfWork = unitOfWork;
            _tokenHelper = tokenHelper;
            _emailService = emailService;
            _smsService = smsService;
            _cache = cache;
            _adminRegistry = adminRegistry;
            _logger = logger;
        }

        public async Task<AuthResponseDto> RegisterAsync(RegisterRequestDto request)
        {
            var email = request.Email?.Trim() ?? string.Empty;
            var firstName = request.FirstName?.Trim() ?? string.Empty;
            var lastName = request.LastName?.Trim() ?? string.Empty;
            ValidatePasswordStrength(request.Password);

            // Les adresses administrateur ne peuvent pas être créées par inscription libre : sans vérification de
            // l'e-mail, n'importe qui pourrait s'inscrire avec une adresse de la liste et devenir administrateur.
            if (_adminRegistry.IsAdminEmail(email))
            {
                _logger.LogWarning("Inscription refusée : adresse réservée à l'équipe IsMoc.");
                throw new ArgumentException("Cette adresse e-mail est réservée. Connectez-vous ou contactez l'équipe IsMoc.");
            }

            var existingUser = await _unitOfWork.Users.GetByEmailAsync(email);
            if (existingUser != null)
            {
                throw new ArgumentException("Un utilisateur avec cet email existe déjà.");
            }

            var newUser = new User
            {
                FirstName = firstName,
                LastName = lastName,
                Email = email,
                PhoneNumber = request.PhoneNumber?.Trim() ?? string.Empty,
                PasswordHash = PasswordHelper.HashPassword(request.Password),
                // Toujours « Client » : le rôle n'est jamais choisi par le visiteur.
                Role = "Client"
            };

            await _unitOfWork.Users.AddAsync(newUser);
            await _unitOfWork.CompleteAsync();

            var token = _tokenHelper.GenerateJwtToken(newUser);

            // E-mail de bienvenue : informatif, il ne doit jamais faire échouer une inscription déjà enregistrée.
            await TrySendNotificationAsync(
                email,
                "Bienvenue sur IsMoc Studio ! 🎉",
                "Ton compte a été créé avec succès. Tu es prêt à modéliser l'application de tes rêves !"
            );

            return new AuthResponseDto
            {
                Token = token,
                Email = newUser.Email,
                FullName = $"{newUser.FirstName} {newUser.LastName}",
                Role = newUser.Role
            };
        }

        public async Task<AuthResponseDto> LoginAsync(LoginRequestDto request)
        {
            var user = await _unitOfWork.Users.GetByEmailAsync(request.Email?.Trim() ?? string.Empty);
            if (user == null)
            {
                throw new Exception("Identifiants incorrects.");
            }

            // Mot de passe copié depuis un e-mail (ex. mot de passe temporaire) : un espace ou un retour à la ligne
            // invisible est souvent collé avec. On accepte aussi la version sans espaces autour.
            var password = request.Password ?? string.Empty;
            var trimmedPassword = password.Trim();
            bool isPasswordValid = PasswordHelper.VerifyPassword(password, user.PasswordHash)
                || (trimmedPassword.Length > 0 && trimmedPassword != password && PasswordHelper.VerifyPassword(trimmedPassword, user.PasswordHash));
            if (!isPasswordValid)
            {
                throw new Exception("Identifiants incorrects.");
            }

            await ApplyAdminPolicyAsync(user);
            var token = _tokenHelper.GenerateJwtToken(user);

            // Alerte de connexion : informative, une panne SMTP ne doit pas empêcher de se connecter.
            await TrySendNotificationAsync(
                user.Email,
                "Nouvelle connexion à votre compte",
                "Une nouvelle connexion a été détectée sur votre compte IsMoc Studio."
            );

            return new AuthResponseDto
            {
                Token = token,
                Email = user.Email,
                FullName = $"{user.FirstName} {user.LastName}",
                Role = user.Role
            };
        }

        /// <summary>Message d'échec d'envoi : le mot de passe actuel n'a PAS été modifié.</summary>
        public const string ResetEmailFailedMessage =
            "L'e-mail de réinitialisation n'a pas pu être envoyé pour le moment. Votre mot de passe actuel n'a pas été modifié : réessayez dans quelques minutes.";

        /// <summary>
        /// Mot de passe oublié, parcours sécurisé :
        /// 1. Adresse inconnue → aucune erreur renvoyée (on ne révèle pas quels e-mails possèdent un compte).
        /// 2. Mot de passe temporaire aléatoire cryptographique (12 caractères, lettres + chiffres).
        /// 3. L'e-mail est envoyé AVANT toute écriture : si l'envoi SMTP échoue, la base n'est pas touchée
        ///    et l'ancien mot de passe reste le seul valide (l'utilisateur n'est jamais bloqué).
        /// 4. Seulement après la confirmation d'envoi, le nouveau mot de passe (haché) est enregistré.
        /// </summary>
        public async Task ForgotPasswordAsync(string email)
        {
            var normalizedEmail = email?.Trim() ?? string.Empty;
            if (normalizedEmail.Length == 0) throw new ArgumentException("Indiquez votre adresse e-mail.");

            var user = await _unitOfWork.Users.GetByEmailAsync(normalizedEmail);
            if (user == null)
            {
                _logger.LogInformation("Mot de passe oublié demandé pour une adresse sans compte.");
                return;
            }

            var tempPassword = GenerateTemporaryPassword();
            var firstName = WebUtility.HtmlEncode(user.FirstName ?? string.Empty);

            // 1. Envoi de l'e-mail : aucune modification en base tant qu'il n'est pas parti.
            try
            {
                await _emailService.SendEmailAsync(
                    user.Email,
                    "Réinitialisation de votre mot de passe - IsMoc Studio 🔑",
                    $"<p>Bonjour {firstName},</p><p>Une demande de réinitialisation de mot de passe a été effectuée sur votre compte IsMoc Studio.</p>"
                    + $"<p>Votre mot de passe temporaire est : <strong>{tempPassword}</strong></p>"
                    + "<p>Connectez-vous avec ce mot de passe, puis modifiez-le dans « Paramètres ». Si vous n'êtes pas à l'origine de cette demande, contactez l'équipe IsMoc.</p>"
                );
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Échec de l'envoi de l'e-mail de réinitialisation : mot de passe NON modifié (utilisateur {UserId}).", user.Id);
                throw new InvalidOperationException(ResetEmailFailedMessage, exception);
            }

            // 2. Envoi confirmé par le serveur SMTP : le mot de passe temporaire devient actif.
            user.PasswordHash = PasswordHelper.HashPassword(tempPassword);
            _unitOfWork.Users.Update(user);
            await _unitOfWork.CompleteAsync();
        }

        /// <summary>Mot de passe temporaire : 12 caractères tirés par RandomNumberGenerator, au moins une lettre et un chiffre.</summary>
        private static string GenerateTemporaryPassword(int length = 12)
        {
            // Sans caractères ambigus (0/O, 1/l/I) pour une saisie sans erreur.
            const string alphabet = "abcdefghjkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";
            while (true)
            {
                var characters = new char[length];
                for (var index = 0; index < length; index++) characters[index] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
                var password = new string(characters);
                if (password.Any(char.IsLetter) && password.Any(char.IsDigit)) return password;
            }
        }

        /// <summary>E-mail informatif (bienvenue, alerte de connexion) : un échec est journalisé, jamais bloquant.</summary>
        private async Task TrySendNotificationAsync(string toEmail, string subject, string body)
        {
            try
            {
                await _emailService.SendEmailAsync(toEmail, subject, body);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "E-mail de notification « {Subject} » non envoyé.", subject);
            }
        }

        public async Task<AuthResponseDto> GoogleLoginAsync(string email, string firstName, string lastName)
        {
            var user = await _unitOfWork.Users.GetByEmailAsync(email);

            if (user == null)
            {
                user = new User
                {
                    Email = email,
                    FirstName = firstName ?? "Utilisateur",
                    LastName = lastName ?? "Google",
                    PasswordHash = PasswordHelper.HashPassword(Guid.NewGuid().ToString()),
                    Role = "Client"
                };

                await _unitOfWork.Users.AddAsync(user);
                await _unitOfWork.CompleteAsync();

                await TrySendNotificationAsync(
                    email,
                    "Bienvenue sur IsMoc Studio ! 🎉",
                    "Ton compte a été créé avec succès via Google."
                );
            }
            else
            {
                await TrySendNotificationAsync(
                    user.Email,
                    "Nouvelle connexion à votre compte via Google",
                    "Une nouvelle connexion a été détectée sur votre compte IsMoc Studio via Google."
                );
            }

            await ApplyAdminPolicyAsync(user);
            var token = _tokenHelper.GenerateJwtToken(user);

            return new AuthResponseDto
            {
                Token = token,
                Email = user.Email,
                FullName = $"{user.FirstName} {user.LastName}",
                Role = user.Role
            };
        }

        /// <summary>Nombre maximal d'essais de code SMS avant invalidation (anti brute-force).</summary>
        private const int MaxOtpAttempts = 5;

        private static string OtpCacheKey(string phoneNumber) => $"otp:{phoneNumber}";
        private static string OtpAttemptsKey(string phoneNumber) => $"otp-attempts:{phoneNumber}";

        /// <summary>Mot de passe : 8 à 128 caractères, au moins une lettre et un chiffre.</summary>
        private static void ValidatePasswordStrength(string? password)
        {
            if (string.IsNullOrEmpty(password) || password.Length is < 8 or > 128
                || !password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            {
                throw new ArgumentException("Le mot de passe doit contenir entre 8 et 128 caractères, dont au moins une lettre et un chiffre.");
            }
        }

        public async Task SendSmsOtpAsync(string phoneNumber)
        {
            // Code à 6 chiffres tiré par un générateur cryptographique (Random est prévisible).
            var otpCode = System.Security.Cryptography.RandomNumberGenerator.GetInt32(100000, 1000000).ToString();

            _cache.Set(OtpCacheKey(phoneNumber), otpCode, TimeSpan.FromMinutes(5));
            _cache.Remove(OtpAttemptsKey(phoneNumber));

            await _smsService.SendSmsAsync(phoneNumber, $"Votre code de vérification IsMoc Studio est : {otpCode}. Il est valide 5 minutes.");
        }

        public async Task<AuthResponseDto> VerifySmsOtpAsync(string phoneNumber, string code)
        {
            if (!_cache.TryGetValue(OtpCacheKey(phoneNumber), out string? savedCode) || savedCode is null)
            {
                throw new Exception("Code invalide ou expiré.");
            }

            // Comparaison à temps constant + 5 essais maximum : au-delà, le code est détruit (anti brute-force).
            var isValid = code is not null && System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                System.Text.Encoding.UTF8.GetBytes(savedCode), System.Text.Encoding.UTF8.GetBytes(code));
            if (!isValid)
            {
                var attempts = (_cache.TryGetValue(OtpAttemptsKey(phoneNumber), out int previous) ? previous : 0) + 1;
                if (attempts >= MaxOtpAttempts)
                {
                    _cache.Remove(OtpCacheKey(phoneNumber));
                    _cache.Remove(OtpAttemptsKey(phoneNumber));
                    throw new Exception("Trop d'essais : demandez un nouveau code.");
                }

                _cache.Set(OtpAttemptsKey(phoneNumber), attempts, TimeSpan.FromMinutes(5));
                throw new Exception("Code invalide ou expiré.");
            }

            _cache.Remove(OtpCacheKey(phoneNumber));
            _cache.Remove(OtpAttemptsKey(phoneNumber));

            var user = await _unitOfWork.Users.GetByPhoneNumberAsync(phoneNumber);

            if (user == null)
            {
                user = new User
                {
                    PhoneNumber = phoneNumber,
                    FirstName = "Nouveau",
                    LastName = "Client SMS",
                    Email = $"{phoneNumber}@sms.ismoc.com",
                    PasswordHash = PasswordHelper.HashPassword(Guid.NewGuid().ToString()),
                    Role = "Client"
                };

                await _unitOfWork.Users.AddAsync(user);
                await _unitOfWork.CompleteAsync();
            }

            await ApplyAdminPolicyAsync(user);
            var token = _tokenHelper.GenerateJwtToken(user);

            return new AuthResponseDto
            {
                Token = token,
                Email = user.Email,
                FullName = $"{user.FirstName} {user.LastName}",
                Role = user.Role
            };
        }

        // ------------------------------------------------------------------
        // Compte connecté (onglet Paramètres)
        // ------------------------------------------------------------------

        public async Task<AccountProfileDto> GetProfileAsync(int userId)
        {
            var user = await _unitOfWork.Users.GetByIdAsync(userId)
                ?? throw new KeyNotFoundException("Utilisateur introuvable.");
            await ApplyAdminPolicyAsync(user);

            return new AccountProfileDto
            {
                Id = user.Id,
                FirstName = user.FirstName,
                LastName = user.LastName,
                FullName = $"{user.FirstName} {user.LastName}".Trim(),
                Email = user.Email,
                PhoneNumber = user.PhoneNumber,
                Role = user.Role,
                CreatedAt = user.CreatedAt,
                // Jeton à jour : un compte promu Admin récupère son rôle sans se reconnecter.
                Token = _tokenHelper.GenerateJwtToken(user)
            };
        }

        /// <summary>
        /// Rôle Admin si l'e-mail figure dans AdminEmails (ou si le compte est déjà Admin en base,
        /// auquel cas son e-mail rejoint la liste). Appelé avant chaque génération de JWT pour que
        /// le claim ClaimTypes.Role reflète toujours ce rôle.
        /// </summary>
        private async Task ApplyAdminPolicyAsync(User user)
        {
            if (AdminBootstrapper.IsAdmin(user))
            {
                await _adminRegistry.AddAsync(user.Email);
                return;
            }

            if (!_adminRegistry.IsAdminEmail(user.Email)) return;

            user.Role = AdminBootstrapper.Role;
            _unitOfWork.Users.Update(user);
            await _unitOfWork.CompleteAsync();
        }

        public async Task<AuthResponseDto> UpdateProfileAsync(int userId, UpdateProfileRequestDto request)
        {
            var firstName = request.FirstName?.Trim() ?? string.Empty;
            var lastName = request.LastName?.Trim() ?? string.Empty;
            if (firstName.Length is < 1 or > 60 || lastName.Length is < 1 or > 60)
            {
                throw new ArgumentException("Le prénom et le nom sont obligatoires (60 caractères maximum).");
            }

            var phoneNumber = request.PhoneNumber?.Trim();
            if (phoneNumber is { Length: > 0 } && !System.Text.RegularExpressions.Regex.IsMatch(phoneNumber, @"^\+?[0-9 .-]{8,20}$"))
            {
                throw new ArgumentException("Numéro de téléphone invalide.");
            }

            var user = await _unitOfWork.Users.GetByIdAsync(userId)
                ?? throw new KeyNotFoundException("Utilisateur introuvable.");

            user.FirstName = firstName;
            user.LastName = lastName;
            if (phoneNumber is not null) user.PhoneNumber = phoneNumber;
            _unitOfWork.Users.Update(user);
            await _unitOfWork.CompleteAsync();
            await ApplyAdminPolicyAsync(user);

            return new AuthResponseDto
            {
                Token = _tokenHelper.GenerateJwtToken(user),
                Email = user.Email,
                FullName = $"{user.FirstName} {user.LastName}",
                Role = user.Role
            };
        }

        public async Task ChangePasswordAsync(int userId, ChangePasswordRequestDto request)
        {
            if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8
                || !request.NewPassword.Any(char.IsLetter) || !request.NewPassword.Any(char.IsDigit))
            {
                throw new ArgumentException("Le nouveau mot de passe doit contenir au moins 8 caractères, dont une lettre et un chiffre.");
            }

            var user = await _unitOfWork.Users.GetByIdAsync(userId)
                ?? throw new KeyNotFoundException("Utilisateur introuvable.");

            bool currentIsValid;
            try
            {
                currentIsValid = !string.IsNullOrEmpty(request.CurrentPassword) && PasswordHelper.VerifyPassword(request.CurrentPassword, user.PasswordHash);
            }
            catch (BCrypt.Net.SaltParseException)
            {
                currentIsValid = false;
            }

            if (!currentIsValid) throw new UnauthorizedAccessException("Le mot de passe actuel est incorrect.");
            if (PasswordHelper.VerifyPassword(request.NewPassword, user.PasswordHash))
            {
                throw new ArgumentException("Le nouveau mot de passe doit être différent de l'actuel.");
            }

            user.PasswordHash = PasswordHelper.HashPassword(request.NewPassword);
            _unitOfWork.Users.Update(user);
            await _unitOfWork.CompleteAsync();
        }
    }
}