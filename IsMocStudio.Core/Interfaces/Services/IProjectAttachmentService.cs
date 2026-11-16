namespace IsMocStudio.Core.Interfaces.Services
{
    public sealed record ProjectAttachment(string FileName, string Url, string OriginalName, string ContentType, long Size);

    /// <summary>Pièces jointes de la messagerie projet : App_Data/Attachments/{projectId}/.</summary>
    public interface IProjectAttachmentService
    {
        long MaxFileBytes { get; }
        bool IsAllowed(string fileName);
        Task<ProjectAttachment> SaveAsync(string projectId, Stream content, string originalName, CancellationToken cancellationToken = default);

        long MaxVoiceNoteBytes { get; }

        /// <summary>
        /// Enregistre une note vocale (audio/webm, audio/ogg, audio/mp4, audio/mpeg) après vérification
        /// de sa signature binaire réelle (magic bytes). Lève ArgumentException si le contenu est invalide.
        /// </summary>
        Task<ProjectAttachment> SaveVoiceNoteAsync(string projectId, Stream content, string declaredContentType, CancellationToken cancellationToken = default);

        /// <summary>Chemin physique et métadonnées d'une pièce jointe, ou null si elle n'existe pas.</summary>
        (string Path, ProjectAttachment Attachment)? Find(string projectId, string fileName);

        /// <summary>Valide qu'une URL désigne bien une pièce jointe existante de ce projet.</summary>
        ProjectAttachment? Resolve(string projectId, string? attachmentUrl);
    }
}
