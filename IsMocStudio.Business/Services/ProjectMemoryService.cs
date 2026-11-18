using System.Collections.Concurrent;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using IsMocStudio.Core.DTOs;
using IsMocStudio.Core.Interfaces.Services;

namespace IsMocStudio.Business.Services
{
    /// <summary>
    /// Gère un fichier JSON par projet dans App_Data/Memories/{projectId}.json.
    /// Le dossier est créé automatiquement et les écritures sont atomiques (fichier temporaire + remplacement).
    /// </summary>
    public sealed class ProjectMemoryService : IProjectMemoryService
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> FileLocks = new(StringComparer.OrdinalIgnoreCase);
        private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            // Conserve les accents français lisibles dans les fichiers (é, è, à…) au lieu de séquences é.
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };

        private readonly string _memoryDirectory;

        public ProjectMemoryService(string memoryDirectory)
        {
            if (string.IsNullOrWhiteSpace(memoryDirectory)) throw new ArgumentException("Le dossier des mémoires est obligatoire.", nameof(memoryDirectory));
            _memoryDirectory = memoryDirectory;
            Directory.CreateDirectory(_memoryDirectory);
        }

        public string NewProjectId() => Guid.NewGuid().ToString("N");

        /// <summary>
        /// GUID déterministe dérivé de « general-{ownerId} » : un seul fil par client, retrouvable sans index,
        /// et compatible avec toutes les validations d'identifiant (mémoire, pièces jointes, hub SignalR).
        /// </summary>
        public string GeneralThreadId(string ownerId) =>
            new Guid(System.Security.Cryptography.MD5.HashData(System.Text.Encoding.UTF8.GetBytes($"general-{ownerId}"))).ToString("N");

        public async Task<ProjectMemoryDto> EnsureGeneralThreadAsync(string ownerId, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(ownerId)) throw new ArgumentException("Propriétaire invalide.", nameof(ownerId));
            var projectId = GeneralThreadId(ownerId);
            var existing = await GetAsync(projectId, cancellationToken);
            if (existing is not null) return existing;

            var thread = new ProjectMemoryDto
            {
                ProjectId = projectId,
                OwnerId = ownerId,
                CreationMode = ProjectThreadTypes.General,
                ThreadType = ProjectThreadTypes.General,
                ProjectName = ProjectThreadTypes.GeneralName,
                Status = ProjectStatuses.Draft,
                ClientMessages =
                [
                    new ClientMessageDto
                    {
                        Author = "team",
                        AuthorName = "Équipe IsMoc",
                        Content = "Bonjour et bienvenue chez IsMoc Studio ! Posez-nous toutes vos questions avant de lancer votre projet : nous répondons ici, et vous pouvez aussi nous appeler en audio ou en vidéo."
                    }
                ]
            };

            try
            {
                await CreateAsync(thread, cancellationToken);
                return thread;
            }
            catch (IOException)
            {
                // Créé entre-temps par une requête concurrente.
                return await GetAsync(projectId, cancellationToken) ?? thread;
            }
        }

        public async Task CreateAsync(ProjectMemoryDto memory, CancellationToken cancellationToken = default)
        {
            ValidateProjectId(memory.ProjectId);
            var path = GetPath(memory.ProjectId);
            var fileLock = GetLock(path);
            await fileLock.WaitAsync(cancellationToken);
            try
            {
                if (File.Exists(path)) throw new IOException("Une mémoire existe déjà pour cet identifiant de projet.");
                memory.CreatedAt = DateTimeOffset.UtcNow;
                memory.UpdatedAt = memory.CreatedAt;
                await WriteAtomicallyAsync(path, memory, cancellationToken);
            }
            finally
            {
                fileLock.Release();
            }
        }

        public async Task<ProjectMemoryDto?> GetAsync(string projectId, CancellationToken cancellationToken = default)
        {
            ValidateProjectId(projectId);
            var path = GetPath(projectId);
            var fileLock = GetLock(path);
            await fileLock.WaitAsync(cancellationToken);
            try
            {
                if (!File.Exists(path)) return null;
                return await ReadFileAsync(path, cancellationToken);
            }
            finally
            {
                fileLock.Release();
            }
        }

        public Task<IReadOnlyList<ProjectMemoryDto>> ListByOwnerAsync(string ownerId, CancellationToken cancellationToken = default) =>
            ListAsync(memory => string.Equals(memory.OwnerId, ownerId, StringComparison.Ordinal), cancellationToken);

        public Task<IReadOnlyList<ProjectMemoryDto>> ListAllAsync(CancellationToken cancellationToken = default) =>
            ListAsync(_ => true, cancellationToken);

        private async Task<IReadOnlyList<ProjectMemoryDto>> ListAsync(Func<ProjectMemoryDto, bool> predicate, CancellationToken cancellationToken)
        {
            var result = new List<ProjectMemoryDto>();
            if (!Directory.Exists(_memoryDirectory)) return result;

            foreach (var path in Directory.EnumerateFiles(_memoryDirectory, "*.json"))
            {
                var projectId = Path.GetFileNameWithoutExtension(path);
                if (!Guid.TryParseExact(projectId, "N", out _)) continue;

                var fileLock = GetLock(path);
                await fileLock.WaitAsync(cancellationToken);
                try
                {
                    var memory = await ReadFileAsync(path, cancellationToken);
                    if (memory is not null && predicate(memory)) result.Add(memory);
                }
                catch (JsonException)
                {
                    // Un fichier corrompu ne doit pas empêcher l'affichage des autres projets.
                }
                finally
                {
                    fileLock.Release();
                }
            }

            return result.OrderByDescending(memory => memory.UpdatedAt).ToList();
        }

        private static async Task<ProjectMemoryDto?> ReadFileAsync(string path, CancellationToken cancellationToken)
        {
            if (!File.Exists(path)) return null;
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
            var memory = await JsonSerializer.DeserializeAsync<ProjectMemoryDto>(stream, SerializerOptions, cancellationToken);
            if (memory is null) return null;

            // Tolère les anciens fichiers incomplets.
            memory.UserPreferences ??= [];
            memory.ChatHistory ??= [];
            memory.Milestones ??= [];
            memory.ClientMessages ??= [];
            memory.CurrentHtml ??= string.Empty;
            memory.InitialIdea ??= string.Empty;
            memory.ProjectName ??= string.Empty;
            if (string.IsNullOrWhiteSpace(memory.Status)) memory.Status = ProjectStatuses.Draft;
            if (string.IsNullOrWhiteSpace(memory.Format)) memory.Format = "web";
            if (string.IsNullOrWhiteSpace(memory.ThreadType)) memory.ThreadType = ProjectThreadTypes.Project;
            foreach (var message in memory.ClientMessages)
            {
                // Anciens messages (sans type) : déduit de la pièce jointe.
                if (string.IsNullOrWhiteSpace(message.MessageType) || (message.MessageType == ClientMessageTypes.Text && message.AttachmentUrl is not null))
                {
                    message.MessageType = message.AttachmentContentType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true ? ClientMessageTypes.Image
                        : message.AttachmentUrl is not null ? ClientMessageTypes.File
                        : ClientMessageTypes.Text;
                }
            }
            return memory;
        }

        public async Task SaveAsync(ProjectMemoryDto memory, CancellationToken cancellationToken = default)
        {
            ValidateProjectId(memory.ProjectId);
            var path = GetPath(memory.ProjectId);
            var fileLock = GetLock(path);
            await fileLock.WaitAsync(cancellationToken);
            try
            {
                if (!File.Exists(path)) throw new FileNotFoundException("La mémoire du projet est introuvable.");

                // Les messages client sont en ajout seul : on conserve ceux reçus (SignalR) pendant qu'une
                // opération longue (ex. appel IA) travaillait sur une copie plus ancienne de la mémoire.
                var stored = await ReadFileAsync(path, cancellationToken);
                if (stored is not null)
                {
                    var knownIds = memory.ClientMessages.Select(message => message.Id).ToHashSet(StringComparer.Ordinal);
                    var missing = stored.ClientMessages.Where(message => !knownIds.Contains(message.Id)).ToList();
                    if (missing.Count > 0)
                    {
                        memory.ClientMessages.AddRange(missing);
                        memory.ClientMessages.Sort((left, right) => left.SentAt.CompareTo(right.SentAt));
                    }
                }

                memory.UpdatedAt = DateTimeOffset.UtcNow;
                await WriteAtomicallyAsync(path, memory, cancellationToken);
            }
            finally
            {
                fileLock.Release();
            }
        }

        public async Task<ProjectMemoryDto?> UpdateAsync(string projectId, Action<ProjectMemoryDto> mutate, CancellationToken cancellationToken = default)
        {
            ValidateProjectId(projectId);
            var path = GetPath(projectId);
            var fileLock = GetLock(path);
            await fileLock.WaitAsync(cancellationToken);
            try
            {
                var memory = await ReadFileAsync(path, cancellationToken);
                if (memory is null) return null;
                mutate(memory);
                memory.UpdatedAt = DateTimeOffset.UtcNow;
                await WriteAtomicallyAsync(path, memory, cancellationToken);
                return memory;
            }
            finally
            {
                fileLock.Release();
            }
        }

        private static SemaphoreSlim GetLock(string path) => FileLocks.GetOrAdd(path, static _ => new SemaphoreSlim(1, 1));

        private string GetPath(string projectId) => Path.Combine(_memoryDirectory, $"{projectId}.json");

        private async Task WriteAtomicallyAsync(string path, ProjectMemoryDto memory, CancellationToken cancellationToken)
        {
            Directory.CreateDirectory(_memoryDirectory);
            var temporaryPath = $"{path}.{Guid.NewGuid():N}.tmp";
            try
            {
                await using (var stream = new FileStream(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous))
                {
                    await JsonSerializer.SerializeAsync(stream, memory, SerializerOptions, cancellationToken);
                    await stream.FlushAsync(cancellationToken);
                }

                File.Move(temporaryPath, path, true);
            }
            finally
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
        }

        private static void ValidateProjectId(string projectId)
        {
            if (!Guid.TryParseExact(projectId, "N", out _))
            {
                throw new ArgumentException("L’identifiant du projet est invalide.", nameof(projectId));
            }
        }
    }
}
