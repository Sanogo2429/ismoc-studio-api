using IsMocStudio.Core.DTOs;

namespace IsMocStudio.Business.Helpers
{
    /// <summary>
    /// Étapes du suivi client : 1. Brief validé · 2. Payé · 3. En cours de codage · 4. Livré · 5. Maintenance.
    /// </summary>
    public static class ProjectProgress
    {
        public static readonly string[] StepLabels = ["Brief validé", "Payé", "En cours de codage", "Livré", "Maintenance"];

        public static bool IsPaid(string status) =>
            status is ProjectStatuses.InDevelopment or ProjectStatuses.Delivered or ProjectStatuses.Maintenance;

        /// <summary>Étape courante (1 à 5) d'un statut. Un projet payé passe directement en codage (3).</summary>
        public static int StepOf(string status) => status switch
        {
            ProjectStatuses.InDevelopment => 3,
            ProjectStatuses.Delivered => 4,
            ProjectStatuses.Maintenance => 5,
            _ => 1
        };

        /// <summary>
        /// Statut correspondant à une étape fixée par l'équipe. Les étapes 1 et 2 dépendent du client
        /// (validation du brief, paiement) : l'équipe ne peut piloter que 3 à 5, sur un projet payé.
        /// </summary>
        public static string? StatusForStep(int step) => step switch
        {
            3 => ProjectStatuses.InDevelopment,
            4 => ProjectStatuses.Delivered,
            5 => ProjectStatuses.Maintenance,
            _ => null
        };

        public static string DefaultMilestoneTitle(int step) => step switch
        {
            3 => "Développement en cours",
            4 => "Projet livré",
            5 => "Maintenance activée",
            _ => "Nouvelle avancée"
        };

        public static string PaymentStatus(string status) => status switch
        {
            ProjectStatuses.Ordered => "pending",
            _ when IsPaid(status) => "paid",
            _ => "draft"
        };
    }
}
