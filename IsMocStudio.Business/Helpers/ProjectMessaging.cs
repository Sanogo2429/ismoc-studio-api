using System.Text;
using System.Text.RegularExpressions;
using IsMocStudio.Core.DTOs;
using IsMocStudio.Core.Interfaces.Services;

namespace IsMocStudio.Business.Helpers
{
    /// <summary>
    /// Règles communes à la messagerie (hub SignalR et API REST) : assainissement anti-XSS,
    /// stickers autorisés et construction validée d'un message typé.
    /// </summary>
    public static class ProjectMessaging
    {
        public const int MaxTextLength = 2000;
        public const int MaxVoiceSeconds = 300;

        /// <summary>Stickers IsMoc autorisés (identifiant → libellé affiché).</summary>
        public static readonly IReadOnlyDictionary<string, string> Stickers = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["validated"] = "Validé ✅",
            ["great-idea"] = "Super idée 💡",
            ["in-dev"] = "En cours de dev 🚀",
            ["call-me"] = "On s'appelle ? 📞",
            ["thanks"] = "Merci 🙏",
            ["top-design"] = "Top design 🎨",
            ["urgent"] = "Urgent ⚡",
            ["delivered"] = "Livré 🎉",
        };

        private static readonly Regex HtmlTag = new(@"<\s*/?\s*[a-zA-Z!][^>]*>", RegexOptions.Compiled);
        private static readonly Regex DangerousScheme = new(@"\b(?:javascript|vbscript|data)\s*:", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex ExcessBlankLines = new(@"\n{3,}", RegexOptions.Compiled);

        /// <summary>
        /// Assainit un texte utilisateur : retire balises HTML, schémas d'URL dangereux et caractères de contrôle.
        /// (Le frontend échappe déjà l'affichage ; ceci protège aussi les exports, e-mails et futurs clients.)
        /// </summary>
        public static string Sanitize(string? value, int maxLength = MaxTextLength)
        {
            if (string.IsNullOrEmpty(value)) return string.Empty;
            var text = value.Normalize(NormalizationForm.FormC).Replace("\r\n", "\n").Replace('\r', '\n');
            text = new string(text.Where(character => character is '\n' or '\t' || !char.IsControl(character)).ToArray());
            text = HtmlTag.Replace(text, string.Empty);
            text = DangerousScheme.Replace(text, match => match.Value.Replace(":", " :"));
            text = ExcessBlankLines.Replace(text, "\n\n").Trim();
            return text.Length > maxLength ? text[..maxLength] : text;
        }

        /// <summary>
        /// Construit un message validé. Lève ArgumentException (message en français) si la demande est invalide.
        /// Le type « image » / « file » est déduit de la pièce jointe ; « voice » exige une note vocale.
        /// </summary>
        public static ClientMessageDto Create(bool isTeam, string authorName, string? messageType, string? content, ProjectAttachment? attachment, int? audioDurationSeconds)
        {
            var type = string.IsNullOrWhiteSpace(messageType) ? ClientMessageTypes.Text : messageType.Trim().ToLowerInvariant();
            var message = new ClientMessageDto
            {
                Author = isTeam ? "team" : "client",
                AuthorName = Sanitize(authorName, 120),
            };

            switch (type)
            {
                case ClientMessageTypes.Sticker:
                    var stickerId = content?.Trim() ?? string.Empty;
                    if (!Stickers.TryGetValue(stickerId, out var label)) throw new ArgumentException("Sticker inconnu.");
                    message.MessageType = ClientMessageTypes.Sticker;
                    message.Content = stickerId;
                    message.AttachmentName = label;
                    return message;

                case ClientMessageTypes.Voice:
                    if (attachment is null || !attachment.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("Note vocale introuvable.");
                    if (audioDurationSeconds is null or < 1 or > MaxVoiceSeconds) throw new ArgumentException($"Durée de note vocale invalide (1 à {MaxVoiceSeconds} s).");
                    message.MessageType = ClientMessageTypes.Voice;
                    message.AudioDurationSeconds = audioDurationSeconds;
                    break;

                case ClientMessageTypes.Text or ClientMessageTypes.Image or ClientMessageTypes.File:
                    if (attachment?.ContentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase) == true) throw new ArgumentException("Une note vocale doit être envoyée comme message vocal.");
                    message.MessageType = attachment is null ? ClientMessageTypes.Text
                        : attachment.ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) ? ClientMessageTypes.Image
                        : ClientMessageTypes.File;
                    break;

                default:
                    throw new ArgumentException("Type de message invalide.");
            }

            if ((content?.Length ?? 0) > MaxTextLength) throw new ArgumentException($"Le message ne doit pas dépasser {MaxTextLength} caractères.");
            message.Content = Sanitize(content);
            if (message.Content.Length == 0 && attachment is null) throw new ArgumentException("Le message est vide.");

            message.AttachmentUrl = attachment?.Url;
            message.AttachmentName = attachment is null ? null : Sanitize(attachment.OriginalName, 120);
            message.AttachmentContentType = attachment?.ContentType;
            return message;
        }

        /// <summary>Message système « appel manqué » (ajouté par le serveur, jamais envoyé par un client).</summary>
        public static ClientMessageDto CreateMissedCall(string callerName, string callType) => new()
        {
            Author = "system",
            AuthorName = Sanitize(callerName, 120),
            MessageType = ClientMessageTypes.MissedCall,
            Content = callType == "video" ? "video" : "audio",
        };

        /// <summary>Aperçu court d'un message (listes de conversations, notifications).</summary>
        public static string Preview(ClientMessageDto message, int maxLength = 160) => message.MessageType switch
        {
            ClientMessageTypes.Sticker => $"Sticker · {message.AttachmentName ?? message.Content}",
            ClientMessageTypes.Voice => $"🎙️ Message vocal ({message.AudioDurationSeconds ?? 0} s)",
            ClientMessageTypes.MissedCall => message.Content == "video" ? "📞 Appel vidéo manqué" : "📞 Appel audio manqué",
            ClientMessageTypes.Image when string.IsNullOrWhiteSpace(message.Content) => "📷 Photo",
            ClientMessageTypes.File when string.IsNullOrWhiteSpace(message.Content) => $"📎 {message.AttachmentName ?? "Fichier"}",
            _ => message.Content.Length > maxLength ? message.Content[..maxLength] + "…" : message.Content
        };
    }
}
