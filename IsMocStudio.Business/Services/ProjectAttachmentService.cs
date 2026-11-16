using System.Text.RegularExpressions;
using IsMocStudio.Core.Interfaces.Services;

namespace IsMocStudio.Business.Services
{
    /// <summary>Note vocale refusée : conserve le type déclaré et les premiers octets pour le diagnostic (logs).</summary>
    public sealed class InvalidAudioSignatureException : ArgumentException
    {
        public InvalidAudioSignatureException(string message, string declaredType, byte[] data) : base(message)
        {
            DeclaredType = declaredType;
            HeaderHex = Convert.ToHexString(data.AsSpan(0, Math.Min(16, data.Length)));
            Length = data.Length;
        }

        public string DeclaredType { get; }
        public string HeaderHex { get; }
        public int Length { get; }
    }

    /// <summary>
    /// Stocke les fichiers joints du chat dans App_Data/Attachments/{projectId}/{guid}__{nom-original}.
    /// Le nom original est conservé (assaini) dans le nom de fichier pour éviter tout fichier annexe.
    /// </summary>
    public sealed class ProjectAttachmentService : IProjectAttachmentService
    {
        private static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image/png",
            [".jpg"] = "image/jpeg",
            [".jpeg"] = "image/jpeg",
            [".gif"] = "image/gif",
            [".webp"] = "image/webp",
            [".pdf"] = "application/pdf",
            [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
            [".txt"] = "text/plain",
            [".csv"] = "text/csv",
            [".zip"] = "application/zip",
        };

        /// <summary>
        /// Types déclarés acceptés pour une note vocale (paramètres « ;codecs=… » retirés).
        /// Le format réellement stocké est TOUJOURS celui détecté dans les octets, jamais celui déclaré.
        /// Certains navigateurs annoncent video/webm ou video/mp4 pour un enregistrement audio seul.
        /// </summary>
        private static readonly HashSet<string> AcceptedVoiceTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            "audio/webm", "audio/ogg", "audio/mp4", "audio/x-m4a", "audio/m4a", "audio/aac", "audio/mpeg", "audio/mp3",
            "audio/wav", "audio/x-wav", "audio/wave", "video/webm", "video/mp4", "application/octet-stream", ""
        };

        private static readonly Dictionary<string, string> VoiceContentTypes = new(StringComparer.OrdinalIgnoreCase)
        {
            [".webm"] = "audio/webm",
            [".ogg"] = "audio/ogg",
            [".m4a"] = "audio/mp4",
            [".mp3"] = "audio/mpeg",
            [".wav"] = "audio/wav",
        };

        private static readonly Regex StoredName = new(@"^[0-9a-f]{32}__[^/\\]{1,120}$", RegexOptions.Compiled);
        private readonly string _rootDirectory;

        public ProjectAttachmentService(string rootDirectory)
        {
            _rootDirectory = rootDirectory;
            Directory.CreateDirectory(_rootDirectory);
        }

        public long MaxFileBytes => 10 * 1024 * 1024;

        public long MaxVoiceNoteBytes => 5 * 1024 * 1024;

        public async Task<ProjectAttachment> SaveVoiceNoteAsync(string projectId, Stream content, string declaredContentType, CancellationToken cancellationToken = default)
        {
            // « audio/webm;codecs=opus » → « audio/webm »
            var baseType = NormalizeContentType(declaredContentType);
            if (!AcceptedVoiceTypes.Contains(baseType)) throw new ArgumentException("Format audio non pris en charge (webm, ogg, mp4, mpeg ou wav).");

            using var buffer = new MemoryStream();
            await content.CopyToAsync(buffer, cancellationToken);
            if (buffer.Length == 0) throw new ArgumentException("La note vocale est vide.");
            if (buffer.Length > MaxVoiceNoteBytes) throw new ArgumentException("La note vocale dépasse 5 Mo.");

            var bytes = buffer.ToArray();
            // Le format est déduit des octets réels : un HTML, EXE ou ZIP renommé en audio est refusé.
            var extension = DetectAudioExtension(bytes)
                ?? throw new InvalidAudioSignatureException("Le fichier ne correspond pas à un enregistrement audio valide.", baseType, bytes);

            var directory = GetDirectory(projectId);
            Directory.CreateDirectory(directory);
            var fileName = $"{Guid.NewGuid():N}__note-vocale{extension}";
            var path = Path.Combine(directory, fileName);
            await File.WriteAllBytesAsync(path, bytes, cancellationToken);
            return Describe(projectId, fileName, buffer.Length);
        }

        /// <summary>« audio/webm;codecs=opus » → « audio/webm ».</summary>
        public static string NormalizeContentType(string? contentType) =>
            (contentType ?? string.Empty).Split(';')[0].Trim().ToLowerInvariant();

        /// <summary>
        /// Détecte le format audio réel par ses magic bytes ; null si ce n'est pas un audio reconnu
        /// (HTML « &lt; », EXE « MZ », ZIP « PK »… ne correspondent à aucune signature).
        /// </summary>
        public static string? DetectAudioExtension(ReadOnlySpan<byte> data)
        {
            if (data.Length < 12) return null;

            // WebM / Matroska : en-tête EBML 1A 45 DF A3
            if (data[0] == 0x1A && data[1] == 0x45 && data[2] == 0xDF && data[3] == 0xA3) return ".webm";
            // Ogg : « OggS »
            if (data[0] == 0x4F && data[1] == 0x67 && data[2] == 0x67 && data[3] == 0x53) return ".ogg";
            // MP4 / M4A : « ftyp » à l'offset 4
            if (data[4] == 0x66 && data[5] == 0x74 && data[6] == 0x79 && data[7] == 0x70) return ".m4a";
            // WAV : « RIFF » + « WAVE » à l'offset 8
            if (data[0] == 0x52 && data[1] == 0x49 && data[2] == 0x46 && data[3] == 0x46
                && data[8] == 0x57 && data[9] == 0x41 && data[10] == 0x56 && data[11] == 0x45) return ".wav";
            // MP3 : balise « ID3 » ou trame MPEG (FF FB / FF F3 / FF F2…)
            if (data[0] == 0x49 && data[1] == 0x44 && data[2] == 0x33) return ".mp3";
            if (data[0] == 0xFF && data[1] is 0xFB or 0xF3 or 0xF2 or 0xFA) return ".mp3";

            return null;
        }

        public bool IsAllowed(string fileName) => ContentTypes.ContainsKey(Path.GetExtension(fileName ?? string.Empty));

        public async Task<ProjectAttachment> SaveAsync(string projectId, Stream content, string originalName, CancellationToken cancellationToken = default)
        {
            var directory = GetDirectory(projectId);
            Directory.CreateDirectory(directory);

            var safeName = Sanitize(originalName);
            if (!IsAllowed(safeName)) throw new ArgumentException("Type de fichier non autorisé.");

            var fileName = $"{Guid.NewGuid():N}__{safeName}";
            var path = Path.Combine(directory, fileName);
            await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous))
            {
                await content.CopyToAsync(output, cancellationToken);
            }

            return Describe(projectId, fileName, new FileInfo(path).Length);
        }

        public (string Path, ProjectAttachment Attachment)? Find(string projectId, string fileName)
        {
            if (string.IsNullOrWhiteSpace(fileName) || !StoredName.IsMatch(fileName) || Path.GetFileName(fileName) != fileName) return null;
            var path = Path.Combine(GetDirectory(projectId), fileName);
            if (!File.Exists(path)) return null;
            return (path, Describe(projectId, fileName, new FileInfo(path).Length));
        }

        public ProjectAttachment? Resolve(string projectId, string? attachmentUrl)
        {
            if (string.IsNullOrWhiteSpace(attachmentUrl)) return null;
            var prefix = $"/api/Projects/{projectId}/attachments/";
            if (!attachmentUrl.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return null;
            var fileName = Uri.UnescapeDataString(attachmentUrl[prefix.Length..]);
            return Find(projectId, fileName)?.Attachment;
        }

        private static ProjectAttachment Describe(string projectId, string fileName, long size)
        {
            var originalName = fileName[(fileName.IndexOf("__", StringComparison.Ordinal) + 2)..];
            var extension = Path.GetExtension(fileName);
            var contentType = ContentTypes.TryGetValue(extension, out var type) ? type
                : VoiceContentTypes.TryGetValue(extension, out var audioType) ? audioType
                : "application/octet-stream";
            return new ProjectAttachment(fileName, $"/api/Projects/{projectId}/attachments/{Uri.EscapeDataString(fileName)}", originalName, contentType, size);
        }

        private string GetDirectory(string projectId)
        {
            if (!Guid.TryParseExact(projectId, "N", out _)) throw new ArgumentException("L’identifiant du projet est invalide.", nameof(projectId));
            return Path.Combine(_rootDirectory, projectId);
        }

        private static string Sanitize(string? name)
        {
            var fileName = Path.GetFileName(name ?? string.Empty).Trim();
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(fileName.Select(character => invalid.Contains(character) || character is '/' or '\\' ? '-' : character).ToArray());
            cleaned = Regex.Replace(cleaned, @"\s+", "-").Replace("__", "_");
            if (cleaned.Length > 100)
            {
                var extension = Path.GetExtension(cleaned);
                cleaned = cleaned[..(100 - extension.Length)] + extension;
            }
            return string.IsNullOrWhiteSpace(Path.GetFileNameWithoutExtension(cleaned)) ? $"fichier{Path.GetExtension(cleaned)}" : cleaned;
        }
    }
}
