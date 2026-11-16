using System.Text.Json;
using IsMocStudio.Core.Interfaces.Services;
using Microsoft.Extensions.Configuration;

namespace IsMocStudio.Business.Services
{
    /// <summary>
    /// E-mails administrateurs = « AdminEmails » (appsettings*.json, variables d'environnement…)
    /// ∪ e-mails promus automatiquement, persistés dans App_Data/admin-emails.json.
    /// On n'écrit jamais dans appsettings.json au runtime : ce fichier reste versionnable et lisible.
    /// </summary>
    public sealed class AdminAccessRegistry : IAdminAccessRegistry
    {
        private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

        private readonly string _persistedPath;
        private readonly HashSet<string> _configuredEmails;
        private readonly HashSet<string> _persistedEmails;
        private readonly SemaphoreSlim _writeLock = new(1, 1);
        private readonly object _sync = new();

        public AdminAccessRegistry(IConfiguration configuration, string persistedPath)
        {
            _persistedPath = persistedPath;
            _configuredEmails = new HashSet<string>(
                configuration.GetSection("AdminEmails").GetChildren().Select(child => Normalize(child.Value)).Where(email => email.Length > 0),
                StringComparer.OrdinalIgnoreCase);
            _persistedEmails = new HashSet<string>(LoadPersisted(persistedPath), StringComparer.OrdinalIgnoreCase);
        }

        public IReadOnlyCollection<string> Emails
        {
            get
            {
                lock (_sync) return _configuredEmails.Union(_persistedEmails, StringComparer.OrdinalIgnoreCase).OrderBy(email => email).ToList();
            }
        }

        public bool IsAdminEmail(string? email)
        {
            var normalized = Normalize(email);
            if (normalized.Length == 0) return false;
            lock (_sync) return _configuredEmails.Contains(normalized) || _persistedEmails.Contains(normalized);
        }

        public async Task AddAsync(string email, CancellationToken cancellationToken = default)
        {
            var normalized = Normalize(email);
            if (normalized.Length == 0 || IsAdminEmail(normalized)) return;

            await _writeLock.WaitAsync(cancellationToken);
            try
            {
                string[] snapshot;
                lock (_sync)
                {
                    if (!_persistedEmails.Add(normalized)) return;
                    snapshot = _persistedEmails.OrderBy(item => item).ToArray();
                }

                Directory.CreateDirectory(Path.GetDirectoryName(_persistedPath)!);
                var temporaryPath = $"{_persistedPath}.{Guid.NewGuid():N}.tmp";
                await File.WriteAllTextAsync(temporaryPath, JsonSerializer.Serialize(new { AdminEmails = snapshot }, SerializerOptions), cancellationToken);
                File.Move(temporaryPath, _persistedPath, true);
            }
            finally
            {
                _writeLock.Release();
            }
        }

        private static string Normalize(string? email) => email?.Trim().ToLowerInvariant() ?? string.Empty;

        private static IEnumerable<string> LoadPersisted(string path)
        {
            if (!File.Exists(path)) return [];
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                return document.RootElement.TryGetProperty("AdminEmails", out var emails) && emails.ValueKind == JsonValueKind.Array
                    ? emails.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => Normalize(item.GetString())).Where(email => email.Length > 0).ToList()
                    : [];
            }
            catch (JsonException)
            {
                return [];
            }
        }
    }
}
