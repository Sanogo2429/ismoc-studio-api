using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;

namespace IsMocStudio.Core.DTOs
{
    /// <summary>Sérialisation d'une mémoire de projet (base de données et import des anciens fichiers JSON).</summary>
    public static class ProjectMemoryJson
    {
        public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
        {
            // Accents lisibles (é, è, à…) plutôt que des séquences é.
            Encoder = JavaScriptEncoder.Create(UnicodeRanges.All)
        };

        public static string Serialize(ProjectMemoryDto memory) => JsonSerializer.Serialize(memory, Options);

        /// <summary>Désérialise et complète les anciens documents incomplets (null si le JSON est vide).</summary>
        public static ProjectMemoryDto? Deserialize(string json) =>
            JsonSerializer.Deserialize<ProjectMemoryDto>(json, Options) is { } memory ? Normalize(memory) : null;

        /// <summary>Tolère les anciens documents : listes nulles, statut / format / type absents, messages sans type.</summary>
        public static ProjectMemoryDto Normalize(ProjectMemoryDto memory)
        {
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
    }
}
