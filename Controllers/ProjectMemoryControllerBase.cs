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

        /// <summary>Exécute un appel OpenAI et traduit les erreurs en réponses HTTP explicites en français.</summary>
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
                Logger.LogError(exception, "Le service OpenAI n'est pas configuré correctement pour {Operation}.", operation);
                return StatusCode(503, new { Message = exception.Message });
            }
            catch (JsonException exception)
            {
                // Rien n'est sauvegardé tant que le HTML n'est pas valide : la maquette existante reste intacte.
                Logger.LogError(exception, "Le moteur IA a renvoyé un contenu invalide pendant {Operation}.", operation);
                return StatusCode(502, new { Message = $"Le moteur IA n'a pas renvoyé une maquette exploitable pendant {operation}. Votre maquette actuelle est conservée : réessayez.", Code = "AI_INVALID_OUTPUT" });
            }
            catch (Business.Services.AiProviderException exception)
            {
                Logger.LogError(exception, "Le fournisseur {Provider} a échoué ({Status} {Type}) pendant {Operation}.", exception.Provider, exception.StatusCode, exception.ErrorType, operation);

                // Erreur exacte d'Anthropic renvoyée telle quelle (code HTTP, type et message, identiques aux logs),
                // sans bascule ni message reformulé. Aucun secret : la clé API n'apparaît jamais dans ces messages.
                var (httpStatus, code) = exception.StatusCode switch
                {
                    408 => (504, "AI_TIMEOUT"),
                    422 => (422, "AI_REFUSED"),
                    429 or 529 => (503, "AI_SERVICE_BUSY"),
                    401 or 403 or 404 => (503, "AI_PROVIDER_CONFIGURATION"),
                    _ => (502, "AI_PROVIDER_ERROR")
                };
                var providerStatus = exception.StatusCode == 0 ? "réseau injoignable" : $"HTTP {exception.StatusCode}";
                return StatusCode(httpStatus, new
                {
                    Message = $"{exception.Provider} a renvoyé {providerStatus}{(exception.ErrorType is null ? string.Empty : $" ({exception.ErrorType})")} : {exception.Message}",
                    Code = code,
                    exception.Provider,
                    ProviderStatus = exception.StatusCode,
                    ProviderErrorType = exception.ErrorType,
                    ProviderMessage = exception.Message
                });
            }
            catch (ClientResultException exception)
            {
                Logger.LogError(exception, "La requête OpenAI ({Operation}) a échoué avec le statut {StatusCode}.", operation, exception.Status);
                var message = exception.Status switch
                {
                    401 or 403 => "OpenAI a refusé la clé configurée. Vérifiez OpenAI:ApiKey côté backend.",
                    429 => "Le quota ou la limite de requêtes OpenAI est atteint. Vérifiez la facturation et réessayez plus tard.",
                    404 => "Le modèle OpenAI demandé (gpt-4o / gpt-4o-mini) est indisponible pour cette clé ou ce projet OpenAI.",
                    0 => "Le backend n’a pas réussi à joindre OpenAI. Vérifiez la connexion réseau du serveur.",
                    _ => $"OpenAI a refusé {operation} (HTTP {exception.Status}). Consultez les logs du backend."
                };
                // 429 OpenAI (limite de débit ou quota du compte OpenAI) : code métier dédié pour le frontend.
                return exception.Status == 429
                    ? StatusCode(503, new { Message = "Le service IA est très sollicité ou son quota est atteint. Réessayez dans quelques minutes, utilisez le Mode Manuel ou écrivez à l'équipe.", Code = "AI_SERVICE_BUSY" })
                    : StatusCode(502, new { Message = message });
            }
            catch (Exception exception)
            {
                Logger.LogError(exception, "Échec de {Operation} du projet.", operation);
                return StatusCode(502, new { Message = $"Échec de {operation}. Veuillez réessayer." });
            }
        }
    }
}
