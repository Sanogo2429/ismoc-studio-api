using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using IsMocStudio.Core.Interfaces.Services;
using Microsoft.AspNetCore.DataProtection;

namespace IsMocStudio.api.Storage
{
    /// <summary>
    /// Stockage de secours sur le disque (développement, ou Supabase non configuré) : App_Data/Storage/{clé}.
    /// Même logique de liens signés qu'avec Supabase : jeton DataProtection à durée limitée, servi par /api/files/{jeton}.
    /// Sur Render, ce disque est effacé à chaque redéploiement : à n'utiliser qu'en local.
    /// </summary>
    public sealed partial class LocalFileStorage : IFileStorage
    {
        private sealed record LinkPayload(string Key, string ContentType, string? DownloadName);

        private readonly string _root;
        private readonly ITimeLimitedDataProtector _protector;

        public LocalFileStorage(string root, IDataProtectionProvider dataProtection)
        {
            _root = Path.GetFullPath(root);
            _protector = dataProtection.CreateProtector("IsMocStudio.LocalFileStorage.Links").ToTimeLimitedDataProtector();
            Directory.CreateDirectory(_root);
        }

        public string Name => $"disque local ({_root})";

        public async Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default)
        {
            var path = PathFor(key);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
            await content.CopyToAsync(output, cancellationToken);
        }

        public Task<SignedFileLink> CreateSignedLinkAsync(string key, string contentType, TimeSpan lifetime, string? downloadName, CancellationToken cancellationToken = default)
        {
            PathFor(key); // valide la clé
            var token = _protector.Protect(JsonSerializer.Serialize(new LinkPayload(key, contentType, downloadName)), lifetime);
            return Task.FromResult(new SignedFileLink($"/api/files/{token}", DateTimeOffset.UtcNow.Add(lifetime)));
        }

        public Task DeleteAsync(string key, CancellationToken cancellationToken = default)
        {
            var path = PathFor(key);
            if (File.Exists(path)) File.Delete(path);
            return Task.CompletedTask;
        }

        /// <summary>Fichier désigné par un jeton encore valide, ou null (jeton expiré, falsifié ou fichier absent).</summary>
        public (string Path, string ContentType, string? DownloadName)? Open(string token)
        {
            try
            {
                var payload = JsonSerializer.Deserialize<LinkPayload>(_protector.Unprotect(token));
                if (payload is null) return null;
                var path = PathFor(payload.Key);
                return File.Exists(path) ? (path, payload.ContentType, payload.DownloadName) : null;
            }
            catch (Exception exception) when (exception is CryptographicException or JsonException or ArgumentException or FormatException)
            {
                return null;
            }
        }

        /// <summary>Clé « projects/{projectId}/{fichier} » uniquement (ni « .. », ni chemin absolu).</summary>
        private string PathFor(string key)
        {
            if (!SafeKey().IsMatch(key)) throw new ArgumentException("Clé de stockage invalide.", nameof(key));
            var path = Path.GetFullPath(Path.Combine(_root, key.Replace('/', Path.DirectorySeparatorChar)));
            if (!path.StartsWith(_root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) throw new ArgumentException("Clé de stockage invalide.", nameof(key));
            return path;
        }

        [GeneratedRegex(@"^[a-z0-9]+(/[a-z0-9_-]+)*/[a-z0-9_-]+\.[a-z0-9]{1,8}$")]
        private static partial Regex SafeKey();
    }
}
