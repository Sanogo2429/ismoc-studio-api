namespace IsMocStudio.Core.Interfaces.Services
{
    /// <param name="Url">Adresse permanente dans l'API (/api/Projects/{projectId}/attachments/{fileName}), enregistrée dans les messages.</param>
    public sealed record ProjectAttachment(string FileName, string Url, string OriginalName, string ContentType, long Size);

    /// <summary>
    /// Pièces jointes et notes vocales de la messagerie projet : métadonnées en base (table ProjectAttachments),
    /// contenu dans le stockage de fichiers (<see cref="IFileStorage"/>), lecture par lien signé à durée limitée.
    /// Les droits sur le projet sont vérifiés par l'appelant (contrôleur / hub) avant tout appel.
    /// </summary>
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

        /// <summary>Métadonnées d'une pièce jointe de ce projet, ou null si elle n'existe pas.</summary>
        Task<ProjectAttachment?> FindAsync(string projectId, string fileName, CancellationToken cancellationToken = default);

        /// <summary>Valide qu'une URL désigne bien une pièce jointe existante de ce projet.</summary>
        Task<ProjectAttachment?> ResolveAsync(string projectId, string? attachmentUrl, CancellationToken cancellationToken = default);

        /// <summary>Lien signé temporaire (10 min par défaut) ; <paramref name="download"/> force le téléchargement sous le nom original.</summary>
        Task<SignedFileLink?> CreateLinkAsync(string projectId, string fileName, bool download, CancellationToken cancellationToken = default);
    }
}
