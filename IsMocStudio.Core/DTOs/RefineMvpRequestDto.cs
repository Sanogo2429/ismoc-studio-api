namespace IsMocStudio.Core.DTOs
{
    public class RefineMvpRequestDto
    {
        public string ProjectId { get; set; } = string.Empty;
        public string Instruction { get; set; } = string.Empty;

        /// <summary>Capture Figma / croquis optionnel, en data URL (data:image/png;base64,…) ou base64 brut.</summary>
        public string? ImageBase64 { get; set; }
    }
}
