using IsMocStudio.Core.Entities;
using IsMocStudio.Core.Helpers;
using IsMocStudio.Core.Interfaces.Services;
using IsMocStudio.Data.Context;
using Microsoft.EntityFrameworkCore;

namespace IsMocStudio.Data.Services
{
    /// <summary>
    /// Pièces jointes du chat : métadonnées en base (ProjectAttachments), contenu dans <see cref="IFileStorage"/>
    /// (bucket privé Supabase en production). Aucune URL de stockage n'est jamais enregistrée dans les messages :
    /// seule l'adresse permanente de l'API l'est, et chaque lecture passe par un lien signé de courte durée.
    /// </summary>
    public sealed class StoredProjectAttachmentService : IProjectAttachmentService
    {
        public const string FileKind = "file";
        public const string VoiceKind = "voice";

        private readonly IDbContextFactory<IsMocDbContext> _contextFactory;
        private readonly IFileStorage _storage;
        private readonly TimeSpan _linkLifetime;

        public StoredProjectAttachmentService(IDbContextFactory<IsMocDbContext> contextFactory, IFileStorage storage, TimeSpan linkLifetime)
        {
            _contextFactory = contextFactory;
            _storage = storage;
            _linkLifetime = linkLifetime;
        }

        public long MaxFileBytes => AttachmentRules.MaxFileBytes;

        public long MaxVoiceNoteBytes => AttachmentRules.MaxVoiceNoteBytes;

        public bool IsAllowed(string fileName) => AttachmentRules.IsAllowed(fileName);

        public async Task<ProjectAttachment> SaveAsync(string projectId, Stream content, string originalName, CancellationToken cancellationToken = default)
        {
            ValidateProjectId(projectId);
            var safeName = AttachmentRules.Sanitize(originalName);
            if (!AttachmentRules.IsAllowed(safeName)) throw new ArgumentException("Type de fichier non autorisé.");

            var bytes = await ReadLimitedAsync(content, MaxFileBytes, "Le fichier dépasse 10 Mo.", cancellationToken);
            if (bytes.Length == 0) throw new ArgumentException("Le fichier est vide.");
            return await StoreAsync(projectId, safeName, AttachmentRules.ContentTypeFor(safeName), FileKind, bytes, cancellationToken);
        }

        public async Task<ProjectAttachment> SaveVoiceNoteAsync(string projectId, Stream content, string declaredContentType, CancellationToken cancellationToken = default)
        {
            ValidateProjectId(projectId);
            // « audio/webm;codecs=opus » → « audio/webm »
            var baseType = AttachmentRules.NormalizeContentType(declaredContentType);
            if (!AttachmentRules.IsAcceptedVoiceType(baseType)) throw new ArgumentException("Format audio non pris en charge (webm, ogg, mp4, mpeg ou wav).");

            var bytes = await ReadLimitedAsync(content, MaxVoiceNoteBytes, "La note vocale dépasse 5 Mo.", cancellationToken);
            if (bytes.Length == 0) throw new ArgumentException("La note vocale est vide.");

            // Le format est déduit des octets réels : un HTML, EXE ou ZIP renommé en audio est refusé.
            var extension = AttachmentRules.DetectAudioExtension(bytes)
                ?? throw new InvalidAudioSignatureException("Le fichier ne correspond pas à un enregistrement audio valide.", baseType, bytes);

            var name = $"note-vocale{extension}";
            return await StoreAsync(projectId, name, AttachmentRules.ContentTypeFor(name), VoiceKind, bytes, cancellationToken);
        }

        public async Task<ProjectAttachment?> FindAsync(string projectId, string fileName, CancellationToken cancellationToken = default)
        {
            var record = await FindRecordAsync(projectId, fileName, cancellationToken);
            return record is null ? null : Describe(record);
        }

        public async Task<ProjectAttachment?> ResolveAsync(string projectId, string? attachmentUrl, CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(attachmentUrl)) return null;
            var prefix = $"/api/Projects/{projectId}/attachments/";
            if (!attachmentUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            var fileName = Uri.UnescapeDataString(attachmentUrl[prefix.Length..]);
            return await FindAsync(projectId, fileName, cancellationToken);
        }

        public async Task<SignedFileLink?> CreateLinkAsync(string projectId, string fileName, bool download, CancellationToken cancellationToken = default)
        {
            var record = await FindRecordAsync(projectId, fileName, cancellationToken);
            if (record is null) return null;
            return await _storage.CreateSignedLinkAsync(record.StorageKey, record.ContentType, _linkLifetime, download ? record.OriginalName : null, cancellationToken);
        }

        /// <summary>Clé de stockage ASCII (les noms originaux accentués restent en base, pas dans la clé).</summary>
        public static string StorageKeyFor(string projectId, Guid id, string extension) =>
            $"projects/{projectId}/{id:N}{extension.ToLowerInvariant()}";

        private async Task<ProjectAttachment> StoreAsync(string projectId, string originalName, string contentType, string kind, byte[] bytes, CancellationToken cancellationToken)
        {
            var id = Guid.NewGuid();
            var record = new ProjectAttachmentRecord
            {
                FileName = $"{id:N}__{originalName}",
                ProjectId = projectId,
                StorageKey = StorageKeyFor(projectId, id, Path.GetExtension(originalName)),
                OriginalName = originalName,
                ContentType = contentType,
                Size = bytes.Length,
                Kind = kind,
                CreatedAt = DateTimeOffset.UtcNow
            };

            // Fichier d'abord : une ligne en base ne désigne jamais un fichier absent.
            using (var stream = new MemoryStream(bytes, writable: false))
            {
                await _storage.PutAsync(record.StorageKey, stream, contentType, cancellationToken);
            }

            try
            {
                await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
                db.ProjectAttachments.Add(record);
                await db.SaveChangesAsync(cancellationToken);
            }
            catch
            {
                // Échec de l'enregistrement : on ne laisse pas de fichier orphelin dans le stockage.
                await _storage.DeleteAsync(record.StorageKey, CancellationToken.None);
                throw;
            }

            return Describe(record);
        }

        private async Task<ProjectAttachmentRecord?> FindRecordAsync(string projectId, string fileName, CancellationToken cancellationToken)
        {
            if (!Guid.TryParseExact(projectId, "N", out _)) return null;
            if (string.IsNullOrWhiteSpace(fileName) || !AttachmentRules.StoredName.IsMatch(fileName) || Path.GetFileName(fileName) != fileName) return null;
            await using var db = await _contextFactory.CreateDbContextAsync(cancellationToken);
            return await db.ProjectAttachments.AsNoTracking()
                .SingleOrDefaultAsync(record => record.FileName == fileName && record.ProjectId == projectId, cancellationToken);
        }

        private static ProjectAttachment Describe(ProjectAttachmentRecord record) =>
            new(record.FileName, $"/api/Projects/{record.ProjectId}/attachments/{Uri.EscapeDataString(record.FileName)}", record.OriginalName, record.ContentType, record.Size);

        /// <summary>Lit le flux en mémoire en refusant tout dépassement de taille (même si l'en-tête annonçait moins).</summary>
        private static async Task<byte[]> ReadLimitedAsync(Stream content, long maxBytes, string tooLargeMessage, CancellationToken cancellationToken)
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await content.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > maxBytes) throw new ArgumentException(tooLargeMessage);
                buffer.Write(chunk, 0, read);
            }
            return buffer.ToArray();
        }

        private static void ValidateProjectId(string projectId)
        {
            if (!Guid.TryParseExact(projectId, "N", out _)) throw new ArgumentException("L’identifiant du projet est invalide.", nameof(projectId));
        }
    }
}
