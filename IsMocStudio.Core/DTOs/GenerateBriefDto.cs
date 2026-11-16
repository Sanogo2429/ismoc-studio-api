namespace IsMocStudio.Core.DTOs
{
    /// <summary>
    /// Brief de création d'un projet.
    /// Mode IA : seul <see cref="Description"/> est utilisé.
    /// Mode Manuel : format, secteur, palette et pages/modules choisis pas à pas, plus une description optionnelle.
    /// </summary>
    public class GenerateBriefDto
    {
        public string Description { get; set; } = string.Empty;

        /// <summary>"web" ou "mobile".</summary>
        public string? Format { get; set; }

        public string? Sector { get; set; }

        /// <summary>Couleur hexadécimale, ex. #F59E0B.</summary>
        public string? PrimaryColor { get; set; }

        /// <summary>Couleur hexadécimale, ex. #0F172A.</summary>
        public string? SecondaryColor { get; set; }

        /// <summary>Pages / modules à inclure (Accueil, Catalogue, Réservation…).</summary>
        public List<string> Modules { get; set; } = [];

        /// <summary>Capture Figma / croquis optionnel, en data URL (data:image/png;base64,…) ou base64 brut.</summary>
        public string? ImageBase64 { get; set; }
    }
}
