using IsMocStudio.Core.DTOs;

namespace IsMocStudio.Core.Interfaces.Services
{
    /// <summary>Image de référence (capture Figma, croquis) transmise au modèle de vision.</summary>
    public sealed record ReferenceImage(byte[] Data, string MediaType);

    public interface IOpenAiMvpService
    {
        /// <summary>Mode IA : génère une interface HTML5 + Tailwind CDN + JS vanilla à partir d'une idée libre (et d'une image optionnelle).</summary>
        Task<string> GenerateMvpHtmlAsync(string initialIdea, ReferenceImage? image = null);

        /// <summary>Mode Manuel : génère une interface fidèle aux choix exacts (format, secteur, palette, pages/modules, description).</summary>
        Task<string> GenerateManualMvpHtmlAsync(GenerateBriefDto brief, ReferenceImage? image = null);

        /// <summary>Retouche ou question : retourne un JSON strict { reply, html|null, extractedPreference|null }.</summary>
        Task<string> RefineMvpAsync(ProjectMemoryDto memory, string instruction, ReferenceImage? image = null);

        /// <summary>Devis : retourne un JSON strict (montants en euros).</summary>
        Task<string> EstimateMvpAsync(ProjectMemoryDto memory);
    }
}
