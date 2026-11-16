using IsMocStudio.Business.Helpers;
using IsMocStudio.Core.DTOs;
using IsMocStudio.Core.Interfaces.Services;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IsMocStudio.api.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    [Authorize] // Sécurisé : l'utilisateur doit être connecté (JWT requis)
    public class ProjectsController : ProjectMemoryControllerBase
    {
        private const string FirstMockupReply = "Votre première maquette est prête dans le canvas. Demandez-moi une retouche, posez une question ou demandez une estimation du tarif.";
        private const int MaxModules = 12;

        /// <summary>Longueur maximale de la description d'un projet (Mode IA et Mode Manuel).</summary>
        private const int MaxDescriptionLength = 8000;

        private static readonly Regex HexColor = new(@"^#[0-9a-fA-F]{6}$", RegexOptions.Compiled);

        /// <summary>
        /// Détecte une demande de prix / délais sur le projet (et non une retouche du type « ajoute un prix sur chaque produit »).
        /// </summary>
        private static readonly Regex PricingQuestion = new(
            @"\b(devis|chiffrage|chiffrer|estimation|estimer|estime)\b"
            + @"|\bcombien\b.*\b(co[uû]te|co[uû]terait|co[uû]t|prix|tarif|budget|temps|jours|semaines|mois|faut)"
            + @"|\bquel(?:s|le|les)?\b[^?.!]{0,30}\b(prix|tarifs?|co[uû]ts?|budget|d[ée]lais?)\b"
            + @"|\b(prix|tarifs?|co[uû]ts?|budget|d[ée]lais?)\b[^?.!]{0,25}\b(projet|site|application|app|appli|d[ée]veloppement|r[ée]alisation|livraison|maquette|mvp)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        /// <summary>Une phrase qui commence par un verbe de retouche reste une retouche (« affiche le prix du projet en gros »).</summary>
        private static readonly Regex EditCommand = new(
            @"^\s*(ajoute|ajouter|change|changer|modifie|modifier|affiche|afficher|mets?|mettre|supprime|supprimer|retire|retirer|remplace|remplacer|d[ée]place|rends|cr[ée]e|int[eè]gre|agrandis|r[ée]duis)\b",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

        private static readonly JsonSerializerOptions WebJson = new(JsonSerializerDefaults.Web);

        private readonly IProjectService _projectService;
        private readonly IAiQuotaService _aiQuotaService;

        public ProjectsController(IProjectService projectService, IOpenAiMvpService openAiMvpService, IProjectMemoryService projectMemoryService, IAiQuotaService aiQuotaService, ILogger<ProjectsController> logger)
            : base(openAiMvpService, projectMemoryService, logger)
        {
            _projectService = projectService;
            _aiQuotaService = aiQuotaService;
        }

        // ------------------------------------------------------------------
        // Crédits IA (protection des tokens OpenAI)
        // ------------------------------------------------------------------

        /// <summary>Crédits illimités pour les Admin et dès qu'un projet de l'utilisateur est payé.</summary>
        private async Task<bool> HasUnlimitedAiAsync(string userId)
        {
            if (IsAdmin()) return true;
            var memories = await ProjectMemoryService.ListByOwnerAsync(userId, HttpContext.RequestAborted);
            return memories.Any(memory => ProjectProgress.IsPaid(memory.Status));
        }

        /// <summary>Consomme un crédit (et applique la limite par minute) ; retourne la réponse d'erreur métier si refusé.</summary>
        private async Task<IActionResult?> ConsumeAiCreditAsync(string userId, AiUsageKind kind)
        {
            var unlimited = await HasUnlimitedAiAsync(userId);
            var decision = await _aiQuotaService.TryConsumeAsync(userId, kind, unlimited, applyRateLimit: !IsAdmin(), HttpContext.RequestAborted);
            var status = _aiQuotaService.GetStatus(userId, unlimited);
            return decision switch
            {
                AiQuotaDecision.QuotaExceeded => StatusCode(StatusCodes.Status429TooManyRequests, new
                {
                    Message = kind == AiUsageKind.Generation
                        ? $"Vous avez utilisé vos {status.GenerationsLimit} générations IA gratuites. Utilisez le Mode Manuel (gratuit et illimité) ou échangez avec l'équipe dans le chat."
                        : $"Vous avez utilisé vos {status.RefinementsLimit} retouches IA gratuites. Échangez avec l'équipe dans le chat ou commandez votre projet pour un accès illimité.",
                    Code = "AI_QUOTA_EXCEEDED",
                    Quota = QuotaPayload(status)
                }),
                AiQuotaDecision.RateLimited => StatusCode(StatusCodes.Status429TooManyRequests, new
                {
                    Message = $"Trop de demandes IA en peu de temps ({status.MaxRequestsPerMinute} par minute maximum). Patientez quelques secondes.",
                    Code = "AI_SERVICE_BUSY",
                    Quota = QuotaPayload(status)
                }),
                _ => null
            };
        }

        private static object QuotaPayload(AiQuotaStatus status) => new
        {
            status.Unlimited,
            status.RemainingGenerations,
            status.RemainingRefinements,
            status.GenerationsLimit,
            status.RefinementsLimit
        };

        // GET: api/Projects/ai-quota
        [HttpGet("ai-quota")]
        public async Task<IActionResult> GetAiQuota()
        {
            var userId = CurrentUserId();
            if (userId is null) return Unauthorized(new { Message = "Utilisateur non identifié." });
            return Utf8Json(QuotaPayload(_aiQuotaService.GetStatus(userId, await HasUnlimitedAiAsync(userId))));
        }

        private static bool IsPricingQuestion(string instruction) => PricingQuestion.IsMatch(instruction) && !EditCommand.IsMatch(instruction);

        private static bool IsDesignLocked(ProjectMemoryDto memory) =>
            memory.Status is ProjectStatuses.InDevelopment or ProjectStatuses.Delivered or ProjectStatuses.Maintenance;

        // ------------------------------------------------------------------
        // Mode IA : idée libre
        // ------------------------------------------------------------------

        // POST: api/Projects/analyze-idea
        [HttpPost("analyze-idea")]
        [HttpPost("/api/Project/analyze-idea")]
        public Task<IActionResult> AnalyzeIdea([FromBody] GenerateBriefDto request)
        {
            var hasImage = !string.IsNullOrWhiteSpace(request?.ImageBase64);
            if (request is null || (string.IsNullOrWhiteSpace(request.Description) && !hasImage))
            {
                return Task.FromResult<IActionResult>(BadRequest(new { Message = "Décrivez votre projet ou importez une image de maquette." }));
            }

            if (request.Description?.Length > MaxDescriptionLength)
            {
                return Task.FromResult<IActionResult>(BadRequest(new { Message = $"La description ne doit pas dépasser {MaxDescriptionLength} caractères." }));
            }

            var userId = CurrentUserId();
            var consumed = false;
            return ExecuteAiAsync("la génération", async () =>
            {
                if (userId is null) return Unauthorized(new { Message = "Utilisateur non identifié." });

                // ArgumentException (image invalide) → 400 via ExecuteAiAsync.
                var image = Business.Services.OpenAiMvpService.DecodeReferenceImage(request.ImageBase64);

                var quotaError = await ConsumeAiCreditAsync(userId, AiUsageKind.Generation);
                if (quotaError is not null) return quotaError;
                consumed = true;
                var idea = string.IsNullOrWhiteSpace(request.Description)
                    ? "Transforme la maquette jointe en application interactive complète."
                    : request.Description.Trim();
                var html = await OpenAiMvpService.GenerateMvpHtmlAsync(idea, image);
                if (image is not null) idea = $"{idea} (à partir d'une image de référence importée)";
                var memory = await CreateMemoryAsync(userId, "ai", ProjectPricing.FormatFromHtml(html), idea, [], html);
                return Utf8Json(new { projectId = memory.ProjectId, currentHtml = html, reply = FirstMockupReply });
            },
            // Génération échouée : le crédit est restitué.
            onFailure: () => consumed && userId is not null ? _aiQuotaService.RefundAsync(userId, AiUsageKind.Generation) : Task.CompletedTask);
        }

        // ------------------------------------------------------------------
        // Mode Manuel : formulaire guidé pas à pas
        // ------------------------------------------------------------------

        // POST: api/Projects/generate-manual
        [HttpPost("generate-manual")]
        public Task<IActionResult> GenerateManual([FromBody] GenerateBriefDto request)
        {
            var validationError = ValidateManualBrief(request);
            if (validationError is not null)
            {
                return Task.FromResult<IActionResult>(BadRequest(new { Message = validationError }));
            }

            var brief = new GenerateBriefDto
            {
                Format = request.Format!.Trim().ToLowerInvariant(),
                Sector = request.Sector!.Trim(),
                PrimaryColor = request.PrimaryColor!.Trim().ToUpperInvariant(),
                SecondaryColor = request.SecondaryColor!.Trim().ToUpperInvariant(),
                Modules = request.Modules
                    .Where(module => !string.IsNullOrWhiteSpace(module))
                    .Select(module => module.Trim())
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
                Description = request.Description?.Trim() ?? string.Empty
            };

            return ExecuteAiAsync("la génération", async () =>
            {
                var userId = CurrentUserId();
                if (userId is null) return Unauthorized(new { Message = "Utilisateur non identifié." });

                var formatLabel = brief.Format == "mobile" ? "Application mobile" : "Site / application web";
                var initialIdea = new StringBuilder()
                    .Append($"{formatLabel} pour le secteur « {brief.Sector} ».")
                    .Append($" Pages / modules : {string.Join(", ", brief.Modules)}.")
                    .Append($" Palette : {brief.PrimaryColor} (principale) et {brief.SecondaryColor} (secondaire).");
                if (!string.IsNullOrWhiteSpace(brief.Description)) initialIdea.Append($" Description : {brief.Description}");

                List<string> preferences =
                [
                    $"Format : {formatLabel}",
                    $"Secteur d'activité : {brief.Sector}",
                    $"Couleur principale : {brief.PrimaryColor}",
                    $"Couleur secondaire : {brief.SecondaryColor}",
                    $"Pages / modules à conserver : {string.Join(", ", brief.Modules)}"
                ];

                var image = Business.Services.OpenAiMvpService.DecodeReferenceImage(request.ImageBase64);

                // Mode Manuel : gratuit et illimité, seule la limite anti-spam s'applique.
                var quotaError = await ConsumeAiCreditAsync(userId, AiUsageKind.Untracked);
                if (quotaError is not null) return quotaError;

                var html = await OpenAiMvpService.GenerateManualMvpHtmlAsync(brief, image);
                var memory = await CreateMemoryAsync(userId, "manual", brief.Format!, initialIdea.ToString(), preferences, html);
                return Utf8Json(new { projectId = memory.ProjectId, currentHtml = html, reply = FirstMockupReply });
            });
        }

        // ------------------------------------------------------------------
        // Mémoire persistante
        // ------------------------------------------------------------------

        // GET: api/Projects/{projectId}/memory
        [HttpGet("{projectId}/memory")]
        public async Task<IActionResult> GetMemory(string projectId)
        {
            if (!Guid.TryParseExact(projectId, "N", out _)) return BadRequest(new { Message = "Identifiant de projet invalide." });

            var memory = await GetAccessibleMemoryAsync(projectId, allowAdmin: true);
            if (memory is null) return NotFound(new { Message = "Projet introuvable." });

            return Utf8Json(new
            {
                memory.ProjectId,
                memory.ThreadType,
                memory.ProjectName,
                memory.Format,
                memory.Status,
                memory.CreationMode,
                memory.InitialIdea,
                memory.UserPreferences,
                memory.CurrentHtml,
                memory.ChatHistory,
                memory.Quote,
                memory.Order,
                memory.Milestones,
                memory.CreatedAt,
                memory.UpdatedAt
            });
        }

        // ------------------------------------------------------------------
        // Chat intelligent : retouche, question ou devis
        // ------------------------------------------------------------------

        // POST: api/Projects/refine
        [HttpPost("refine")]
        public Task<IActionResult> Refine([FromBody] RefineMvpRequestDto request)
        {
            if (request is null || !Guid.TryParseExact(request.ProjectId, "N", out _) || string.IsNullOrWhiteSpace(request.Instruction))
            {
                return Task.FromResult<IActionResult>(BadRequest(new { Message = "Un projectId valide et une instruction sont obligatoires." }));
            }

            if (request.Instruction.Length > 2000)
            {
                return Task.FromResult<IActionResult>(BadRequest(new { Message = "L'instruction ne doit pas dépasser 2000 caractères." }));
            }

            var userId = CurrentUserId();
            var consumed = false;
            return ExecuteAiAsync("la retouche", async () =>
            {
                if (userId is null) return Unauthorized(new { Message = "Utilisateur non identifié." });
                var memory = await GetAccessibleMemoryAsync(request.ProjectId);
                if (memory is null) return NotFound(new { Message = "Projet introuvable." });
                if (IsGeneralThread(memory)) return GeneralThreadNotSupported();

                var instruction = request.Instruction.Trim();
                var image = Business.Services.OpenAiMvpService.DecodeReferenceImage(request.ImageBase64);

                // Question de prix / délais : on répond par un devis structuré sans toucher à la maquette.
                if (image is null && IsPricingQuestion(instruction))
                {
                    var rateError = await ConsumeAiCreditAsync(userId, AiUsageKind.Untracked);
                    if (rateError is not null) return rateError;

                    memory.ChatHistory.Add(new ChatMessageDto { Role = "user", Content = instruction });
                    var (estimateReply, estimate) = await BuildEstimateAsync(memory);
                    return Utf8Json(new { reply = estimateReply, html = (string?)null, extractedPreference = (string?)null, estimate });
                }

                if (IsDesignLocked(memory))
                {
                    return Conflict(new { Message = "Le design de ce projet a été validé et est en cours de développement. Écrivez à l'équipe depuis votre espace client pour toute évolution." });
                }

                var quotaError = await ConsumeAiCreditAsync(userId, AiUsageKind.Refinement);
                if (quotaError is not null) return quotaError;
                consumed = true;

                memory.ChatHistory.Add(new ChatMessageDto { Role = "user", Content = image is null ? instruction : $"🖼️ [Image de référence jointe] {instruction}" });
                var responseJson = await OpenAiMvpService.RefineMvpAsync(memory, instruction, image);
                var response = JsonSerializer.Deserialize<RefineMvpResponseDto>(responseJson, WebJson)
                    ?? throw new JsonException("Réponse de retouche vide.");

                // CurrentHtml et UserPreferences ne changent que lors d'une retouche visuelle.
                if (!string.IsNullOrWhiteSpace(response.Html))
                {
                    memory.CurrentHtml = response.Html;
                    var title = ProjectPricing.TitleFromHtml(response.Html);
                    if (!string.IsNullOrWhiteSpace(title)) memory.ProjectName = title;

                    var preference = response.ExtractedPreference?.Trim();
                    if (!string.IsNullOrWhiteSpace(preference)
                        && !memory.UserPreferences.Contains(preference, StringComparer.OrdinalIgnoreCase))
                    {
                        memory.UserPreferences.Add(preference);
                    }

                    // Une commande non payée doit être re-chiffrée sur la nouvelle maquette.
                    if (memory.Status == ProjectStatuses.Ordered) memory.Status = ProjectStatuses.Draft;
                }
                else
                {
                    response.Html = null;
                    response.ExtractedPreference = null;
                }

                memory.ChatHistory.Add(new ChatMessageDto { Role = "assistant", Content = response.Reply });
                await ProjectMemoryService.SaveAsync(memory, HttpContext.RequestAborted);
                return Utf8Json(new { reply = response.Reply, html = response.Html, extractedPreference = response.ExtractedPreference });
            },
            onFailure: () => consumed && userId is not null ? _aiQuotaService.RefundAsync(userId, AiUsageKind.Refinement) : Task.CompletedTask);
        }

        // POST: api/Projects/estimate (bouton « Estimer le tarif »)
        [HttpPost("estimate")]
        public Task<IActionResult> Estimate([FromBody] EstimateMvpRequestDto request)
        {
            if (request is null || !Guid.TryParseExact(request.ProjectId, "N", out _))
            {
                return Task.FromResult<IActionResult>(BadRequest(new { Message = "Un projectId valide est obligatoire pour établir un devis." }));
            }

            return ExecuteAiAsync("l'estimation", async () =>
            {
                var memory = await GetAccessibleMemoryAsync(request.ProjectId);
                if (memory is null) return NotFound(new { Message = "Projet introuvable." });
                if (IsGeneralThread(memory)) return GeneralThreadNotSupported();

                var rateError = await ConsumeAiCreditAsync(CurrentUserId()!, AiUsageKind.Untracked);
                if (rateError is not null) return rateError;

                var message = string.IsNullOrWhiteSpace(request.Message)
                    ? "Estime le tarif et les délais de ce projet."
                    : request.Message.Trim();
                memory.ChatHistory.Add(new ChatMessageDto { Role = "user", Content = message });

                var (reply, estimate) = await BuildEstimateAsync(memory);
                return Utf8Json(new { reply, estimate });
            });
        }

        // GET: api/Projects/studio — projets IsMoc de l'utilisateur (maquettes, commandes, suivis)
        [HttpGet("studio")]
        public async Task<IActionResult> GetStudioProjects()
        {
            var userId = CurrentUserId();
            if (userId is null) return Unauthorized(new { Message = "Utilisateur non identifié." });

            // Fil « Discussion générale · Avant-projet » : disponible dès l'inscription, sans maquette.
            await ProjectMemoryService.EnsureGeneralThreadAsync(userId, HttpContext.RequestAborted);

            var memories = await ProjectMemoryService.ListByOwnerAsync(userId, HttpContext.RequestAborted);
            return Utf8Json(memories.Select(memory => new
            {
                memory.ProjectId,
                memory.ThreadType,
                ProjectName = string.IsNullOrWhiteSpace(memory.ProjectName) ? "Projet sans nom" : memory.ProjectName,
                memory.Format,
                memory.Status,
                memory.InitialIdea,
                TotalEur = memory.Order?.ProjectTotalEur,
                // Commande complète (échéancier, maintenance, moyen de paiement) pour l'onglet Facturation.
                memory.Order,
                memory.CreatedAt,
                memory.UpdatedAt
            }));
        }

        // ------------------------------------------------------------------
        // Projets en base (existant)
        // ------------------------------------------------------------------

        // POST: api/Projects
        [HttpPost]
        public async Task<IActionResult> CreateProject([FromBody] CreateProjectDto request)
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userIdClaim == null || !int.TryParse(userIdClaim, out var userId))
            {
                return Unauthorized("Utilisateur non identifié dans le token.");
            }

            try
            {
                var createdProject = await _projectService.CreateProjectAsync(userId, request);
                return Ok(new
                {
                    Message = "Projet créé avec succès ! 🎉",
                    Project = createdProject
                });
            }
            catch (Exception ex)
            {
                return BadRequest(new { Message = ex.Message });
            }
        }

        // GET: api/Projects
        [HttpGet]
        public async Task<IActionResult> GetMyProjects()
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userIdClaim == null || !int.TryParse(userIdClaim, out var userId))
            {
                return Unauthorized("Utilisateur non identifié dans le token.");
            }

            var projects = await _projectService.GetUserProjectsAsync(userId);
            return Ok(projects);
        }

        // GET: api/Projects/{id}/brief
        [HttpGet("{id:int}/brief")]
        public async Task<IActionResult> GetProjectBrief(int id)
        {
            var userIdClaim = User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
            if (userIdClaim == null || !int.TryParse(userIdClaim, out var userId))
            {
                return Unauthorized("Utilisateur non identifié dans le token.");
            }

            try
            {
                var project = await _projectService.GetProjectByIdAsync(userId, id);
                return Ok(new
                {
                    Message = "Brief JSON récupéré avec succès ! 🧠",
                    ProjectId = project.Id,
                    ProjectName = project.Name,
                    JsonConfiguration = JsonDocument.Parse(project.JsonConfiguration).RootElement
                });
            }
            catch (Exception ex)
            {
                return NotFound(new { Message = ex.Message });
            }
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private async Task<ProjectMemoryDto> CreateMemoryAsync(string userId, string mode, string format, string initialIdea, List<string> preferences, string html)
        {
            var memory = new ProjectMemoryDto
            {
                ProjectId = ProjectMemoryService.NewProjectId(),
                OwnerId = userId,
                CreationMode = mode,
                Format = format == "mobile" ? "mobile" : "web",
                ProjectName = ProjectPricing.TitleFromHtml(html) ?? string.Empty,
                Status = ProjectStatuses.Draft,
                InitialIdea = initialIdea,
                UserPreferences = preferences,
                CurrentHtml = html,
                ChatHistory =
                [
                    new ChatMessageDto { Role = "user", Content = initialIdea },
                    new ChatMessageDto { Role = "assistant", Content = FirstMockupReply }
                ],
                Milestones =
                [
                    new ProjectMilestoneDto { Title = "Maquette générée", Description = "Première version interactive créée avec IsMoc AI." }
                ]
            };
            await ProjectMemoryService.CreateAsync(memory, HttpContext.RequestAborted);
            return memory;
        }

        private static string? ValidateManualBrief(GenerateBriefDto? request)
        {
            if (request is null) return "Le brief du projet est obligatoire.";

            var format = request.Format?.Trim().ToLowerInvariant();
            if (format is not ("web" or "mobile")) return "Le format doit être « web » ou « mobile ».";
            if (string.IsNullOrWhiteSpace(request.Sector) || request.Sector.Trim().Length > 100) return "Le secteur d'activité est obligatoire (100 caractères maximum).";
            if (request.PrimaryColor is null || !HexColor.IsMatch(request.PrimaryColor.Trim())) return "La couleur principale doit être au format hexadécimal (#RRGGBB).";
            if (request.SecondaryColor is null || !HexColor.IsMatch(request.SecondaryColor.Trim())) return "La couleur secondaire doit être au format hexadécimal (#RRGGBB).";

            var modules = request.Modules?.Where(module => !string.IsNullOrWhiteSpace(module)).ToList() ?? [];
            if (modules.Count == 0) return "Sélectionnez au moins une page ou un module.";
            if (modules.Count > MaxModules) return $"Sélectionnez au maximum {MaxModules} pages ou modules.";
            if (modules.Any(module => module.Trim().Length > 60)) return "Chaque page ou module doit faire 60 caractères maximum.";
            if (request.Description?.Length > MaxDescriptionLength) return $"La description ne doit pas dépasser {MaxDescriptionLength} caractères.";

            return null;
        }
    }
}
