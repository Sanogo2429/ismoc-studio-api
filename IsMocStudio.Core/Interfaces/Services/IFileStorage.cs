namespace IsMocStudio.Core.Interfaces.Services
{
    /// <summary>Lien temporaire vers un fichier privé (URL absolue Supabase, ou relative à l'API en stockage local).</summary>
    public sealed record SignedFileLink(string Url, DateTimeOffset ExpiresAt);

    /// <summary>
    /// Stockage des fichiers binaires hors du conteneur (Supabase Storage en production, disque local en développement).
    /// Les fichiers sont privés : on n'y accède que par un lien signé à durée limitée, généré par l'API
    /// après vérification des droits sur le projet.
    /// </summary>
    public interface IFileStorage
    {
        /// <summary>Nom lisible du stockage (journaux de démarrage).</summary>
        string Name { get; }

        Task PutAsync(string key, Stream content, string contentType, CancellationToken cancellationToken = default);

        /// <param name="downloadName">Nom proposé au téléchargement (Content-Disposition: attachment) ; null pour un affichage direct.</param>
        Task<SignedFileLink> CreateSignedLinkAsync(string key, string contentType, TimeSpan lifetime, string? downloadName, CancellationToken cancellationToken = default);

        Task DeleteAsync(string key, CancellationToken cancellationToken = default);
    }
}
