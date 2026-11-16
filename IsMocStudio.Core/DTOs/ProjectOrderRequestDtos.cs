namespace IsMocStudio.Core.DTOs
{
    public class FinalizeOrderRequestDto
    {
        /// <summary>"none", "monthly" (par défaut) ou "annual".</summary>
        public string? MaintenancePlan { get; set; }
    }

    /// <summary>
    /// Confirmation d'un paiement simulé. Le numéro de carte complet et le CVC ne sont jamais transmis au backend.
    /// </summary>
    public class ConfirmPaymentRequestDto
    {
        public string MaintenancePlan { get; set; } = "monthly";

        /// <summary>"card" ou "mobile_money".</summary>
        public string PaymentMethod { get; set; } = "card";
        public string? CardholderName { get; set; }
        public string? CardLast4 { get; set; }

        /// <summary>Opérateur Mobile Money ou "virement".</summary>
        public string? MobileProvider { get; set; }
    }

    /// <summary>Avancée publiée par l'équipe (POST /api/Projects/{id}/progress).</summary>
    public class ProjectProgressRequestDto
    {
        /// <summary>Nouvelle étape 3 (codage), 4 (livré) ou 5 (maintenance). Null : étape inchangée.</summary>
        public int? Step { get; set; }

        /// <summary>Titre du jalon ; facultatif si l'étape change (titre par défaut).</summary>
        public string? Title { get; set; }
        public string? Description { get; set; }
    }

    public class ClientMessageRequestDto
    {
        public string Content { get; set; } = string.Empty;

        /// <summary>URL renvoyée par POST /api/Projects/{projectId}/attachments ou /voice-notes.</summary>
        public string? AttachmentUrl { get; set; }

        /// <summary>"text" (défaut), "sticker" (Content = identifiant du sticker) ou "voice".</summary>
        public string? MessageType { get; set; }
        public int? AudioDurationSeconds { get; set; }
    }
}
