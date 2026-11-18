namespace IsMocStudio.Core.DTOs
{
    public class ChatMessageDto
    {
        /// <summary>"user" ou "assistant".</summary>
        public string Role { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
    }

    /// <summary>Statuts du cycle de vie d'un projet.</summary>
    public static class ProjectStatuses
    {
        public const string Draft = "draft";                 // Maquette en cours de conception
        public const string Ordered = "ordered";             // Commande finalisée, paiement en attente
        public const string InDevelopment = "in_development"; // Payé, en cours de codage
        public const string Delivered = "delivered";         // Livré
        public const string Maintenance = "maintenance";     // Sous contrat de maintenance
    }

    public static class ProjectThreadTypes
    {
        public const string Project = "project";
        public const string General = "general";
        public const string GeneralName = "Discussion générale · Avant-projet";
    }

    /// <summary>Types de messages de la messagerie client ↔ équipe.</summary>
    public static class ClientMessageTypes
    {
        public const string Text = "text";
        public const string Image = "image";
        public const string File = "file";
        public const string Sticker = "sticker";
        public const string Voice = "voice";

        /// <summary>Message système « appel manqué » : Content = « audio » ou « video », AuthorName = l'appelant.</summary>
        public const string MissedCall = "missed_call";
    }

    /// <summary>
    /// Mémoire persistante d'un projet, sérialisée dans App_Data/Memories/{projectId}.json.
    /// </summary>
    public class ProjectMemoryDto
    {
        public string ProjectId { get; set; } = string.Empty;
        public string OwnerId { get; set; } = string.Empty;

        /// <summary>"ai" (idée libre), "manual" (formulaire guidé) ou "general" (fil de discussion avant-projet).</summary>
        public string CreationMode { get; set; } = "ai";

        /// <summary>"project" (maquette / commande) ou "general" (discussion générale · avant-projet, une par client).</summary>
        public string ThreadType { get; set; } = ProjectThreadTypes.Project;

        /// <summary>Nom commercial du projet (déduit de la maquette ou du devis).</summary>
        public string ProjectName { get; set; } = string.Empty;

        /// <summary>"web" ou "mobile".</summary>
        public string Format { get; set; } = "web";

        public string Status { get; set; } = ProjectStatuses.Draft;
        public string InitialIdea { get; set; } = string.Empty;
        public List<string> UserPreferences { get; set; } = [];
        public string CurrentHtml { get; set; } = string.Empty;
        public List<ChatMessageDto> ChatHistory { get; set; } = [];

        /// <summary>Dernière évaluation chiffrée en euros.</summary>
        public ProjectQuoteDto? Quote { get; set; }

        /// <summary>Commande et échéancier de paiement.</summary>
        public ProjectOrderDto? Order { get; set; }

        public List<ProjectMilestoneDto> Milestones { get; set; } = [];
        public List<ClientMessageDto> ClientMessages { get; set; } = [];
        public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    public class RefineMvpResponseDto
    {
        public string Reply { get; set; } = string.Empty;
        public string? Html { get; set; }
        public string? ExtractedPreference { get; set; }
    }

    /// <summary>Évaluation chiffrée structurée, exclusivement en euros.</summary>
    public class ProjectQuoteDto
    {
        public string Currency { get; set; } = "EUR";
        public string ComplexityLevel { get; set; } = string.Empty;
        public int DevelopmentPriceEur { get; set; }
        public int DesignIdentityPriceEur { get; set; }
        public int MonthlyMaintenanceEur { get; set; }
        public int EstimatedDays { get; set; }
        public int MinDays { get; set; }
        public int MaxDays { get; set; }
        public int TotalMinEur { get; set; }
        public int TotalMaxEur { get; set; }
        public List<QuoteBreakdownDto> Breakdown { get; set; } = [];
        public List<string> Screens { get; set; } = [];
        public List<string> Features { get; set; } = [];
        public string Summary { get; set; } = string.Empty;

        /// <summary>Empreinte SHA-256 du HTML chiffré : un devis est recalculé si la maquette a changé.</summary>
        public string HtmlFingerprint { get; set; } = string.Empty;
        public DateTimeOffset EstimatedAt { get; set; } = DateTimeOffset.UtcNow;
    }

    public class QuoteBreakdownDto
    {
        public string Workstream { get; set; } = string.Empty;
        public int MinDays { get; set; }
        public int MaxDays { get; set; }
        public int MinPriceEur { get; set; }
        public int MaxPriceEur { get; set; }
    }

    public class ProjectOrderDto
    {
        public string InvoiceNumber { get; set; } = string.Empty;

        /// <summary>"none", "monthly" ou "annual".</summary>
        public string MaintenancePlan { get; set; } = "monthly";

        /// <summary>Montant de la maintenance pour la période (0 si aucune).</summary>
        public decimal MaintenanceAmountEur { get; set; }

        /// <summary>"mois", "an" ou null.</summary>
        public string? MaintenancePeriod { get; set; }

        public decimal DevelopmentPriceEur { get; set; }
        public decimal DesignIdentityPriceEur { get; set; }

        /// <summary>Total projet (développement + identité visuelle), payable en 3 fois.</summary>
        public decimal ProjectTotalEur { get; set; }

        public List<InstallmentDto> Installments { get; set; } = [];

        /// <summary>"card" ou "mobile_money".</summary>
        public string? PaymentMethod { get; set; }
        public string? PaymentDetail { get; set; }
        public string? CardholderName { get; set; }
        public string? CardLast4 { get; set; }
        public DateTimeOffset OrderedAt { get; set; } = DateTimeOffset.UtcNow;
        public DateTimeOffset? PaidAt { get; set; }
    }

    public class InstallmentDto
    {
        public string Label { get; set; } = string.Empty;
        public decimal AmountEur { get; set; }

        /// <summary>"paid" ou "pending".</summary>
        public string Status { get; set; } = "pending";
        public DateTimeOffset? PaidAt { get; set; }
    }

    public class ProjectMilestoneDto
    {
        public string Title { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public DateTimeOffset Date { get; set; } = DateTimeOffset.UtcNow;
    }

    public class ClientMessageDto
    {
        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        /// <summary>"client", "team" ou "system".</summary>
        public string Author { get; set; } = "client";
        public string AuthorName { get; set; } = string.Empty;
        public string Content { get; set; } = string.Empty;
        public DateTimeOffset SentAt { get; set; } = DateTimeOffset.UtcNow;

        /// <summary>URL relative de la pièce jointe (api/Projects/{projectId}/attachments/{fichier}).</summary>
        public string? AttachmentUrl { get; set; }
        public string? AttachmentName { get; set; }
        public string? AttachmentContentType { get; set; }

        /// <summary>"text", "image", "file", "sticker", "voice" ou "missed_call" (message système).</summary>
        public string MessageType { get; set; } = ClientMessageTypes.Text;

        /// <summary>Durée d'un message vocal, en secondes.</summary>
        public int? AudioDurationSeconds { get; set; }
    }
}
