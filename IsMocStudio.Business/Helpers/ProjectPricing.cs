using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using IsMocStudio.Core.DTOs;

namespace IsMocStudio.Business.Helpers
{
    /// <summary>
    /// Règles de tarification : tous les montants proviennent du devis calculé par l'IA (en euros).
    /// Seules les règles commerciales (remise annuelle, paiement en 3 fois) sont définies ici.
    /// </summary>
    public static class ProjectPricing
    {
        public const decimal AnnualMaintenanceDiscount = 0.15m;
        public static readonly string[] InstallmentLabels = ["Aujourd'hui", "À mi-parcours", "À la livraison"];
        public static readonly string[] MaintenancePlans = ["none", "monthly", "annual"];

        public static bool IsValidPlan(string? plan) => plan is not null && MaintenancePlans.Contains(plan);

        public static (decimal Amount, string? Period) Maintenance(ProjectQuoteDto quote, string plan) => plan switch
        {
            "monthly" => (quote.MonthlyMaintenanceEur, "mois"),
            "annual" => (AnnualPrice(quote.MonthlyMaintenanceEur), "an"),
            _ => (0m, null)
        };

        public static decimal AnnualPrice(int monthlyEur) => Math.Round(monthlyEur * 12m * (1 - AnnualMaintenanceDiscount), 0, MidpointRounding.AwayFromZero);

        public static decimal ProjectTotal(ProjectQuoteDto quote) => quote.DevelopmentPriceEur + quote.DesignIdentityPriceEur;

        /// <summary>Découpe le total en 3 échéances au centime près ; la dernière absorbe l'arrondi.</summary>
        public static List<InstallmentDto> Installments(decimal total)
        {
            var third = Math.Floor(total * 100m / 3m) / 100m;
            return
            [
                new InstallmentDto { Label = InstallmentLabels[0], AmountEur = third },
                new InstallmentDto { Label = InstallmentLabels[1], AmountEur = third },
                new InstallmentDto { Label = InstallmentLabels[2], AmountEur = total - 2 * third }
            ];
        }

        public static string Fingerprint(string html) =>
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(html ?? string.Empty)));

        /// <summary>Convertit la réponse JSON (validée) de l'IA en devis structuré.</summary>
        public static ProjectQuoteDto QuoteFromEstimate(JsonElement estimate, string currentHtml)
        {
            static int Int(JsonElement element, string name) => element.TryGetProperty(name, out var value) && value.TryGetInt32(out var number) ? Math.Max(0, number) : 0;
            static List<string> Strings(JsonElement element, string name) =>
                element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Array
                    ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!.Trim()).Where(item => item.Length > 0).Distinct().ToList()
                    : [];

            var delivery = estimate.GetProperty("deliveryTime");
            var total = estimate.GetProperty("totalBudgetEur");
            var minDays = Int(delivery, "minDays");
            var maxDays = Math.Max(minDays, Int(delivery, "maxDays"));

            return new ProjectQuoteDto
            {
                ComplexityLevel = estimate.GetProperty("complexity").GetString() ?? string.Empty,
                DevelopmentPriceEur = Int(estimate, "developmentPriceEur"),
                DesignIdentityPriceEur = Int(estimate, "designIdentityPriceEur"),
                MonthlyMaintenanceEur = Int(estimate, "monthlyMaintenanceEur"),
                EstimatedDays = Int(estimate, "estimatedDays") is var days and > 0 ? days : (minDays + maxDays + 1) / 2,
                MinDays = minDays,
                MaxDays = maxDays,
                TotalMinEur = Int(total, "min"),
                TotalMaxEur = Int(total, "max"),
                Breakdown = estimate.GetProperty("breakdown").EnumerateArray().Select(row => new QuoteBreakdownDto
                {
                    Workstream = row.GetProperty("workstream").GetString() ?? string.Empty,
                    MinDays = Int(row, "minDays"),
                    MaxDays = Int(row, "maxDays"),
                    MinPriceEur = Int(row, "minPriceEur"),
                    MaxPriceEur = Int(row, "maxPriceEur")
                }).ToList(),
                Screens = Strings(estimate, "screens"),
                Features = Strings(estimate, "features"),
                Summary = estimate.GetProperty("summary").GetString() ?? string.Empty,
                HtmlFingerprint = Fingerprint(currentHtml),
                EstimatedAt = DateTimeOffset.UtcNow
            };
        }

        /// <summary>Nom du projet lu dans la balise &lt;title&gt; de la maquette.</summary>
        public static string? TitleFromHtml(string html)
        {
            var match = Regex.Match(html ?? string.Empty, @"<title[^>]*>\s*([^<]{1,120}?)\s*</title>", RegexOptions.IgnoreCase);
            return match.Success ? System.Net.WebUtility.HtmlDecode(match.Groups[1].Value).Trim() : null;
        }

        /// <summary>Une maquette encadrée dans le châssis smartphone est une application mobile.</summary>
        public static string FormatFromHtml(string html)
        {
            var source = html ?? string.Empty;
            string[] phoneMarkers = ["w-[390px]", "h-[812px]", "rounded-[44px]", "max-w-[410px]", "rounded-[42px]"];
            return phoneMarkers.Any(marker => source.Contains(marker, StringComparison.Ordinal)) ? "mobile" : "web";
        }
    }
}
