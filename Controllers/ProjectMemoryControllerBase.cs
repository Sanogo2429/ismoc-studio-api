using System.ClientModel;
using System.Globalization;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using IsMocStudio.Business.Helpers;
using IsMocStudio.Core.DTOs;
using IsMocStudio.Core.Interfaces.Services;
using Microsoft.AspNetCore.Mvc;

namespace IsMocStudio.api.Controllers
{
    /// <summary>
    /// Socle commun des contrôleurs qui manipulent la mémoire persistante d'un projet.
    /// </summary>
    public abstract class ProjectMemoryControllerBase : ControllerBase
    {
        protected static readonly CultureInfo French = CultureInfo.GetCultureInfo("fr-FR");

        protected readonly IOpenAiMvpService OpenAiMvpService;
        protected readonly IProjectMemoryService ProjectMemoryService;
        protected readonly ILogger Logger;

        protected ProjectMemoryControllerBase(IOpenAiMvpService openAiMvpService, IProjectMemoryService projectMemoryService, ILogger logger)
        {
            OpenAiMvpService = openAiMvpService;
            ProjectMemoryService = projectMemoryService;
            Logger = logger;
        }

        protected string? CurrentUserId()
        {
            var userId = User.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? User.FindFirst("sub")?.Value;
            return string.IsNullOrWhiteSpace(userId) ? null : userId;
        }

        protected bool IsAdmin() => User.IsInRole("Admin");

        protected string CurrentUserDisplayName()
        {
            var fullName = $"{User.FindFirst(ClaimTypes.GivenName)?.Value} {User.FindFirst(ClaimTypes.Surname)?.Value}".Trim();
            return string.IsNullOrWhiteSpace(fullName) ? "Client" : fullName;
        }

        /// <summary>Retourne la mémoire si l'utilisateur en est propriétaire (ou administrateur quand <paramref name="allowAdmin"/>).</summary>
        protected async Task<ProjectMemoryDto?> GetAccessibleMemoryAsync(string projectId, bool allowAdmin = false)
        {
            if (!Guid.TryParseExact(projectId, "N", out _)) return null;
            var userId = CurrentUserId();
            if (userId is null) return null;

            var memory = await ProjectMemoryService.GetAsync(projectId, HttpContext.RequestAborted);
            if (memory is null) return null;
            return string.Equals(memory.OwnerId, userId, StringComparison.Ordinal) || (allowAdmin && IsAdmin()) ? memory : null;
        }

        /// <summary>
        /// Contrôle d'accès explicite pour les routes sensibles (messages, fichiers, vocaux) :
        /// 400 identifiant invalide · 404 inexistant · 403 si l'utilisateur n'est ni propriétaire ni Admin.
        /// </summary>
        protected async Task<(ProjectMemoryDto? Memory, IActionResult? Error)> ResolveAccessAsync(string projectId, bool allowAdmin = true)
        {
            if (!Guid.TryParseExact(projectId, "N", out _)) return (null, BadRequest(new { Message = "Identifiant de projet invalide." }));
            var userId = CurrentUserId();
            if (userId is null) return (null, Unauthorized(new { Message = "Utilisateur non identifié." }));

            var memory = await ProjectMemoryService.GetAsync(projectId, HttpContext.RequestAborted);
            if (memory is null) return (null, NotFound(new { Message = "Projet introuvable." }));

            var isOwner = string.Equals(memory.OwnerId, userId, StringComparison.Ordinal);
            if (!isOwner && !(allowAdmin && IsAdmin()))
            {
                Logger.LogWarning("Accès refusé au projet {ProjectId} pour l'utilisateur {UserId}.", projectId, userId);
                return (null, StatusCode(StatusCodes.Status403Forbidden, new { Message = "Accès refusé : ce projet ne vous appartient pas.", Code = "FORBIDDEN" }));
            }

            return (memory, null);
        }

        protected static bool IsGeneralThread(ProjectMemoryDto memory) => memory.ThreadType == ProjectThreadTypes.General;

        protected IActionResult GeneralThreadNotSupported() =>
            BadRequest(new { Message = "La discussion générale ne contient pas de maquette : créez un projet pour utiliser cette fonction.", Code = "GENERAL_THREAD" });

        protected static JsonResult Utf8Json(object value) => new(value)
        {
            ContentType = "application/json; charset=utf-8"
        };

        /// <summary>
        /// Demande le devis en euros à l'IA, l'enregistre dans la mémoire (Quote), l'ajoute à l'historique et sauvegarde.
        /// </summary>
        protected async Task<(string Reply, JsonElement Estimate)> BuildEstimateAsync(ProjectMemoryDto memory, bool addToChat = true)
        {
            var estimateJson = await OpenAiMvpService.EstimateMvpAsync(memory);
            using var document = JsonDocument.Parse(estimateJson);
            var estimate = document.RootElement.Clone();

            memory.Quote = ProjectPricing.QuoteFromEstimate(estimate, memory.CurrentHtml);
            if (estimate.TryGetProperty("projectName", out var name) && name.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(name.GetString()) && string.IsNullOrWhiteSpace(memory.ProjectName))
            {
                memory.ProjectName = name.GetString()!.Trim();
            }

            var reply = FormatEstimate(memory.Quote);
            if (addToChat) memory.ChatHistory.Add(new ChatMessageDto { Role = "assistant", Content = reply });
            await ProjectMemoryService.SaveAsync(memory, HttpContext.RequestAborted);
            return (reply, estimate);
        }

        protected static string Euros(decimal value) =>
            value.ToString(decimal.Truncate(value) == value ? "N0" : "N2", French) + " €";

        private static string FormatEstimate(ProjectQuoteDto quote)
        {
            var builder = new StringBuilder();
            builder.AppendLine("📋 Devis indicatif (en euros)");
            builder.AppendLine($"Complexité : {quote.ComplexityLevel}");
            builder.AppendLine($"Délai estimé : {quote.EstimatedDays} jours ouvrés (fourchette {quote.MinDays}–{quote.MaxDays} j)");
            builder.AppendLine();
            builder.AppendLine("Découpage :");
            foreach (var row in quote.Breakdown)
            {
                builder.AppendLine($"• {row.Workstream} : {row.MinDays}–{row.MaxDays} j · {Euros(row.MinPriceEur)} – {Euros(row.MaxPriceEur)}");
            }
            builder.AppendLine();
            builder.AppendLine($"Développement sur mesure : {Euros(quote.DevelopmentPriceEur)}");
            builder.AppendLine($"Identité visuelle complète : {Euros(quote.DesignIdentityPriceEur)}");
            builder.AppendLine($"Maintenance : {Euros(quote.MonthlyMaintenanceEur)} / mois");
            builder.AppendLine($"Budget total : {Euros(quote.TotalMinEur)} – {Euros(quote.TotalMaxEur)} HT");
            builder.Append(quote.Summary);
            return builder.ToString().Trim();
        }

        /// <summary>Page de l'espace client où écrire à l'équipe (lien proposé avec chaque erreur IA).</summary>
        protected const string MessagesPath = "/dashboard?tab=messages";

        private const string NotChargedSuffix = " Votre crédit IA n'a pas été décompté.";

        /// <summary>
        /// Réponse générique d'indisponibilité de l'IA. Jamais de détail technique (configuration, clé, fournisseur,
        /// code HTTP) côté client : tout est dans les journaux du serveur.
        /// </summary>
        protected ObjectResult AiUnavailable(string code = "AI_UNAVAILABLE", int status = StatusCodes.Status503ServiceUnavailable, string? message = null) =>
            StatusCode(status, new
            {
                Message = (message ?? "IsMoc AI est momentanément indisponible. Réessayez dans quelques minutes ou écrivez à l'équipe depuis Messages.") + NotChargedSuffix,
                Code = code,
                MessagesUrl = MessagesPath
            });

        /// <summary>Exécute un appel au moteur IA et traduit les erreurs en réponses génériques en français (détails dans les journaux).</summary>
        /// <param name="onFailure">Appelé si l'opération échoue (ex. restituer le crédit IA consommé).</param>
        protected async Task<IActionResult> ExecuteAiAsync(string operation, Func<Task<IActionResult>> action, Func<Task>? onFailure = null)
        {
            var result = await ExecuteAiCoreAsync(operation, action);
            if (onFailure is not null && result is ObjectResult { StatusCode: >= 400 } or StatusCodeResult { StatusCode: >= 400 })
            {
                try { await onFailure(); }
                catch (Exception exception) { Logger.LogWarning(exception, "Restitution du crédit IA impossible."); }
            }

            return result;
        }

        private async Task<IActionResult> ExecuteAiCoreAsync(string operation, Func<Task<IActionResult>> action)
        {
            try
            {
                return await action();
            }
            catch (OperationCanceledException) when (HttpContext.RequestAborted.IsCancellationRequested)
            {
                return StatusCode(499, new { Message = "Requête annulée." });
            }
            catch (ArgumentException exception)
            {
                return BadRequest(new { Message = exception.Message });
            }
            catch (InvalidOperationException exception)
            {
                // Configuration du moteur IA (clé absente, modèle…) : détail dans les journaux uniquement.
                Logger.LogError(exception, "Moteur IA mal configuré pendant {Operation}.", operation);
                return AiUnavailable();
            }
            catch (JsonException exception)
            {
                // Rien n'est sauvegardé tant que le HTML n'est pas valide : la maquette existante reste intacte.
                Logger.LogError(exception, "Le moteur IA a renvoyé un contenu invalide pendant {Operation}.", operation);
                return AiUnavailable("AI_INVALID_OUTPUT", StatusCodes.Status502BadGateway, "IsMoc AI n'a pas produit de maquette exploitable. Votre maquette actuelle est conservée : réessayez ou écrivez à l'équipe depuis Messages.");
            }
            catch (Business.Services.AiProviderException exception)
            {
                Logger.LogError(exception, "Le fournisseur {Provider} a échoué ({Status} {Type}) pendant {Operation}.", exception.Provider, exception.StatusCode, exception.ErrorType, operation);

                // Détail du fournisseur (code HTTP, type, message) : journaux uniquement, jamais renvoyé au client.
                return exception.StatusCode switch
                {
                    408 => AiUnavailable("AI_TIMEOUT", StatusCodes.Status504GatewayTimeout, "IsMoc AI a mis trop de temps à répondre. Réessayez dans un instant ou écrivez à l'équipe depuis Messages."),
                    422 => AiUnavailable("AI_REFUSED", StatusCodes.Status422UnprocessableEntity, "IsMoc AI n'a pas pu traiter cette demande. Reformulez-la ou écrivez à l'équipe depuis Messages."),
                    429 or 529 => AiUnavailable("AI_SERVICE_BUSY", message: "IsMoc AI est très sollicité en ce moment. Réessayez dans quelques minutes, utilisez le Mode Manuel ou écrivez à l'équipe depuis Messages."),
                    _ => AiUnavailable()
                };
            }
            catch (ClientResultException exception)
            {
                Logger.LogError(exception, "La requête OpenAI ({Operation}) a échoué avec le statut {StatusCode}.", operation, exception.Status);
                // Clé refusée, modèle indisponible, réseau… : journaux uniquement, message générique au client.
                return exception.Status == 429
                    ? AiUnavailable("AI_SERVICE_BUSY", message: "IsMoc AI est très sollicité en ce moment. Réessayez dans quelques minutes, utilisez le Mode Manuel ou écrivez à l'équipe depuis Messages.")
                    : AiUnavailable();
            }
            catch (Exception exception)
            {
                Logger.LogError(exception, "Échec de {Operation} du projet.", operation);
                return AiUnavailable("AI_UNAVAILABLE", StatusCodes.Status502BadGateway);
            }
        }
    }
}
