using IsMocStudio.Core.Entities;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;

namespace IsMocStudio.Business.Helpers
{
    /// <summary>
    /// Émission et paramètres de validation des JWT.
    /// La clé de signature vient UNIQUEMENT de la configuration (User Secrets en local, variable d'environnement
    /// <c>JwtSettings__Secret</c> en production) : aucune clé par défaut dans le code.
    /// </summary>
    public class TokenHelper
    {
        public const string Issuer = "IsMocStudio.Api";
        public const string Audience = "IsMocStudio.Client";

        /// <summary>HMAC-SHA256 : 32 octets minimum (256 bits).</summary>
        private const int MinimumKeyBytes = 32;
        private const int DefaultExpiryHours = 24;

        private readonly IConfiguration _configuration;

        public TokenHelper(IConfiguration configuration)
        {
            _configuration = configuration;
        }

        /// <summary>Clé de signature lue dans « JwtSettings:Secret » ; erreur explicite si absente ou trop courte.</summary>
        public static byte[] GetSigningKey(IConfiguration configuration)
        {
            var secret = configuration["JwtSettings:Secret"];
            if (string.IsNullOrWhiteSpace(secret))
            {
                throw new InvalidOperationException(
                    "JwtSettings:Secret est absent. Définissez-le avec « dotnet user-secrets set JwtSettings:Secret <clé> » "
                    + "en local, ou la variable d'environnement JwtSettings__Secret en production.");
            }

            var key = Encoding.UTF8.GetBytes(secret);
            if (key.Length < MinimumKeyBytes)
            {
                throw new InvalidOperationException($"JwtSettings:Secret est trop court : {MinimumKeyBytes} octets minimum.");
            }

            return key;
        }

        /// <summary>Paramètres de validation partagés (API REST + hub SignalR).</summary>
        public static TokenValidationParameters CreateValidationParameters(IConfiguration configuration) => new()
        {
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(GetSigningKey(configuration)),
            ValidateIssuer = true,
            ValidIssuer = Issuer,
            ValidateAudience = true,
            ValidAudience = Audience,
            ValidateLifetime = true,
            RequireExpirationTime = true,
            RequireSignedTokens = true,
            ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
            // Tolérance d'horloge réduite (5 min par défaut) : un jeton expiré est refusé presque immédiatement.
            ClockSkew = TimeSpan.FromMinutes(1),
            RoleClaimType = ClaimTypes.Role
        };

        public string GenerateJwtToken(User user)
        {
            var expiryHours = int.TryParse(_configuration["JwtSettings:ExpiryHours"], out var hours) && hours is > 0 and <= 24 * 30
                ? hours
                : DefaultExpiryHours;

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.GivenName, user.FirstName),
                new Claim(ClaimTypes.Surname, user.LastName),
                new Claim(ClaimTypes.Role, user.Role),
                // Identifiant unique du jeton (traçabilité / révocation future).
                new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString("N"))
            };

            var now = DateTime.UtcNow;
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Issuer = Issuer,
                Audience = Audience,
                IssuedAt = now,
                NotBefore = now,
                Expires = now.AddHours(expiryHours),
                SigningCredentials = new SigningCredentials(new SymmetricSecurityKey(GetSigningKey(_configuration)), SecurityAlgorithms.HmacSha256)
            };

            var tokenHandler = new JwtSecurityTokenHandler();
            return tokenHandler.WriteToken(tokenHandler.CreateToken(tokenDescriptor));
        }
    }
}
