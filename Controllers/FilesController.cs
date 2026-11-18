using IsMocStudio.api.Storage;
using IsMocStudio.Core.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IsMocStudio.api.Controllers
{
    /// <summary>
    /// Liens signés du stockage LOCAL (développement) : équivalent des URL signées Supabase.
    /// Le jeton (DataProtection, durée limitée) est l'autorisation : il n'est délivré qu'après vérification
    /// des droits sur le projet (GET …/attachments/{fichier}/link). En production (Supabase), toujours 404.
    /// </summary>
    [ApiController]
    [Route("api/files")]
    [AllowAnonymous]
    public sealed class FilesController : ControllerBase
    {
        private readonly IFileStorage _storage;

        public FilesController(IFileStorage storage) => _storage = storage;

        [HttpGet("{token}")]
        public IActionResult Get(string token)
        {
            if (_storage is not LocalFileStorage local) return NotFound();
            var file = local.Open(token);
            if (file is null) return NotFound(new { Message = "Lien expiré ou invalide." });

            var (path, contentType, downloadName) = file.Value;
            Response.Headers.XContentTypeOptions = "nosniff";
            Response.Headers.CacheControl = "private, max-age=600";
            return downloadName is null
                ? PhysicalFile(path, contentType, enableRangeProcessing: true)
                : PhysicalFile(path, contentType, downloadName, enableRangeProcessing: true);
        }
    }
}
