using System.Text.Json;
using System.Text.RegularExpressions;
using IsMocStudio.api.Hubs;
using IsMocStudio.Business.Helpers;
using IsMocStudio.Core.DTOs;
using IsMocStudio.Core.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.SignalR;

namespace IsMocStudio.api.Controllers
{
    /// <summary>
    /// Tunnel de commande : finalisation, paiement en 3 fois, dossier livrable développeurs et espace client.
    /// </summary>
    [Route("api/Projects")]
    [ApiController]
    [Authorize]
    public class ProjectOrdersController : ProjectMemoryControllerBase
    {
        private const string TeamName = ProjectCommunicationHub.TeamName;
        private static readonly string[] MobileProviders = ["Orange Money", "Wave", "MTN Mobile Money", "Moov Money", "Virement bancaire"];
        private static readonly Regex Last4 = new(@"^\d{4}$", RegexOptions.Compiled);

        private readonly IProjectDeliverableService _deliverableService;
        private readonly IProjectAttachmentService _attachmentService;
        private readonly IHubContext<ProjectCommunicationHub> _hubContext;

        public ProjectOrdersController(
            IOpenAiMvpService openAiMvpService,
            IProjectMemoryService projectMemoryService,
            IProjectDeliverableService deliverableService,
            IProjectAttachmentService attachmentService,
            IHubContext<ProjectCommunicationHub> hubContext,
            ILogger<ProjectOrdersController> logger)
            : base(openAiMvpService, projectMemoryService, logger)
        {
            _deliverableService = deliverableService;
            _attachmentService = attachmentService;
            _hubContext = hubContext;
        }

        private static bool IsPaid(ProjectMemoryDto memory) =>
            memory.Status is ProjectStatuses.InDevelopment or ProjectStatuses.Delivered or ProjectStatuses.Maintenance;

        // ------------------------------------------------------------------
        // Finalisation de la commande
        // ------------------------------------------------------------------

        // POST: api/Projects/{projectId}/finalize-order
        [HttpPost("{projectId}/finalize-order")]
        public Task<IActionResult> FinalizeOrder(string projectId, [FromBody] FinalizeOrderRequestDto? request)
        {
            var plan = string.IsNullOrWhiteSpace(request?.MaintenancePlan) ? "monthly" : request.MaintenancePlan.Trim().ToLowerInvariant();
            if (!ProjectPricing.IsValidPlan(plan))
            {
                return Task.FromResult<IActionResult>(BadRequest(new { Message = "Formule de maintenance invalide (none, monthly ou annual)." }));
            }

            return ExecuteAiAsync("le chiffrage de la commande", async () =>
            {
                var memory = await GetAccessibleMemoryAsync(projectId);
                if (memory is null) return NotFound(new { Message = "Projet introuvable." });
                if (IsGeneralThread(memory)) return GeneralThreadNotSupported();
                if (string.IsNullOrWhiteSpace(memory.CurrentHtml)) return Conflict(new { Message = "La maquette est vide : impossible de commander ce projet." });

                if (IsPaid(memory)) return Utf8Json(CheckoutResponse(memory, memory.Order?.MaintenancePlan ?? plan));

                // Le devis est recalculé si la maquette a changé depuis le dernier chiffrage.
                if (memory.Quote is null || memory.Quote.HtmlFingerprint != ProjectPricing.Fingerprint(memory.CurrentHtml)
                    || memory.Quote.DevelopmentPriceEur <= 0)
                {
                    await BuildEstimateAsync(memory, addToChat: false);
                }

                memory.Order = BuildOrder(memory, plan, memory.Order);
                memory.Status = ProjectStatuses.Ordered;
                await _deliverableService.WriteAsync(memory, HttpContext.RequestAborted);
                await ProjectMemoryService.SaveAsync(memory, HttpContext.RequestAborted);
                return Utf8Json(CheckoutResponse(memory, plan));
            });
        }

        // POST: api/Projects/{projectId}/confirm-payment
        [HttpPost("{projectId}/confirm-payment")]
        public async Task<IActionResult> ConfirmPayment(string projectId, [FromBody] ConfirmPaymentRequestDto request)
        {
            if (request is null) return BadRequest(new { Message = "Les informations de paiement sont obligatoires." });

            var plan = request.MaintenancePlan?.Trim().ToLowerInvariant();
            if (!ProjectPricing.IsValidPlan(plan)) return BadRequest(new { Message = "Formule de maintenance invalide." });

            var method = request.PaymentMethod?.Trim().ToLowerInvariant();
            string paymentDetail;
            if (method == "card")
            {
                if (string.IsNullOrWhiteSpace(request.CardholderName) || request.CardholderName.Trim().Length > 100) return BadRequest(new { Message = "Le nom sur la carte est obligatoire." });
                if (request.CardLast4 is null || !Last4.IsMatch(request.CardLast4)) return BadRequest(new { Message = "Carte bancaire invalide." });
                paymentDetail = $"Carte •••• {request.CardLast4}";
            }
            else if (method == "mobile_money")
            {
                var provider = MobileProviders.FirstOrDefault(item => string.Equals(item, request.MobileProvider?.Trim(), StringComparison.OrdinalIgnoreCase));
                if (provider is null) return BadRequest(new { Message = "Choisissez un opérateur Mobile Money ou le virement bancaire." });
                paymentDetail = provider;
            }
            else
            {
                return BadRequest(new { Message = "Moyen de paiement invalide (card ou mobile_money)." });
            }

            var memory = await GetAccessibleMemoryAsync(projectId);
            if (memory is null) return NotFound(new { Message = "Projet introuvable." });
            if (IsGeneralThread(memory)) return GeneralThreadNotSupported();

            // Idempotent : un double clic ne doit pas créer un second paiement.
            if (IsPaid(memory))
            {
                return Utf8Json(new { projectId = memory.ProjectId, status = memory.Status, invoiceNumber = memory.Order?.InvoiceNumber, alreadyPaid = true });
            }

            if (memory.Quote is null || memory.Order is null || memory.Status != ProjectStatuses.Ordered
                || memory.Quote.HtmlFingerprint != ProjectPricing.Fingerprint(memory.CurrentHtml))
            {
                return Conflict(new { Message = "Le devis n'est plus à jour. Rechargez la page de commande pour le recalculer." });
            }

            var now = DateTimeOffset.UtcNow;
            var order = BuildOrder(memory, plan!, memory.Order);
            order.PaymentMethod = method;
            order.PaymentDetail = paymentDetail;
            order.CardholderName = method == "card" ? request.CardholderName!.Trim() : null;
            order.CardLast4 = method == "card" ? request.CardLast4 : null;
            order.PaidAt = now;
            order.Installments[0].Status = "paid";
            order.Installments[0].PaidAt = now;
            memory.Order = order;
            memory.Status = ProjectStatuses.InDevelopment;

            memory.Milestones.AddRange(
            [
                new ProjectMilestoneDto { Title = "Brief validé", Description = "Le cahier des charges et la maquette ont été validés.", Date = order.OrderedAt },
                new ProjectMilestoneDto { Title = "Design validé", Description = "La maquette interactive a été transmise à l'équipe de développement.", Date = now },
                new ProjectMilestoneDto { Title = "Paiement reçu", Description = $"1ʳᵉ échéance de {Euros(order.Installments[0].AmountEur)} réglée ({paymentDetail}).", Date = now },
                new ProjectMilestoneDto { Title = "Développement démarré", Description = $"Livraison estimée sous {memory.Quote.EstimatedDays} jours ouvrés.", Date = now.AddSeconds(1) }
            ]);

            var firstName = CurrentUserDisplayName().Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "et bienvenue";
            memory.ClientMessages.Add(new ClientMessageDto
            {
                Author = "team",
                AuthorName = TeamName,
                Content = $"Bonjour {firstName}, merci pour votre confiance ! Votre paiement de {Euros(order.Installments[0].AmountEur)} a bien été enregistré et votre projet « {DisplayName(memory)} » est entre les mains de nos développeurs. Livraison estimée sous {memory.Quote.EstimatedDays} jours ouvrés. Écrivez-nous ici à tout moment.",
                SentAt = now.AddSeconds(2)
            });

            await _deliverableService.WriteAsync(memory, HttpContext.RequestAborted);
            await ProjectMemoryService.SaveAsync(memory, HttpContext.RequestAborted);
            Logger.LogInformation("Paiement confirmé pour le projet {ProjectId} ({Amount} €).", memory.ProjectId, order.Installments[0].AmountEur);

            return Utf8Json(new { projectId = memory.ProjectId, status = memory.Status, invoiceNumber = order.InvoiceNumber, paidAmountEur = order.Installments[0].AmountEur });
        }

        // ------------------------------------------------------------------
        // Dossier livrable développeurs
        // ------------------------------------------------------------------

        // GET: api/Projects/{projectId}/deliverable  (?download=true pour l'archive ZIP)
        // Code source (HTML) + cahier des charges : réservé à l'équipe agence, jamais exportable par le client final.
        [HttpGet("{projectId}/deliverable")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> GetDeliverable(string projectId, [FromQuery] bool download = false)
        {
            var memory = await GetAccessibleMemoryAsync(projectId, allowAdmin: true);
            if (memory is null) return NotFound(new { Message = "Projet introuvable." });

            if (!_deliverableService.Exists(projectId))
            {
                return NotFound(new { Message = "Aucun dossier de conception : le projet n'a pas encore été commandé." });
            }

            if (download)
            {
                var zip = await _deliverableService.ZipAsync(projectId, HttpContext.RequestAborted);
                if (zip is null) return NotFound(new { Message = "Dossier de conception introuvable." });
                return File(zip, "application/zip", $"ismoc-{projectId}-dossier-conception.zip");
            }

            var deliverable = await _deliverableService.ReadAsync(projectId, HttpContext.RequestAborted);
            if (deliverable is null) return NotFound(new { Message = "Dossier de conception introuvable." });

            using var specification = JsonDocument.Parse(deliverable.Value.SpecificationJson);
            return Utf8Json(new
            {
                projectId,
                folder = $"App_Data/Deliverables/{projectId}/",
                generatedAt = deliverable.Value.GeneratedAt,
                files = new[] { "design-valide.html", "cahier-des-charges.json" },
                designHtml = deliverable.Value.DesignHtml,
                specification = specification.RootElement.Clone()
            });
        }

        // ------------------------------------------------------------------
        // Espace client : messagerie et avancées
        // ------------------------------------------------------------------

        // GET: api/Projects/{projectId}/client-messages
        [HttpGet("{projectId}/client-messages")]
        public async Task<IActionResult> GetClientMessages(string projectId)
        {
            var (memory, error) = await ResolveAccessAsync(projectId);
            if (error is not null) return error;
            return Utf8Json(memory!.ClientMessages.OrderBy(message => message.SentAt));
        }

        // POST: api/Projects/{projectId}/client-messages  (alternative REST au hub SignalR, diffusée en temps réel)
        [HttpPost("{projectId}/client-messages")]
        public async Task<IActionResult> PostClientMessage(string projectId, [FromBody] ClientMessageRequestDto request)
        {
            var (memory, error) = await ResolveAccessAsync(projectId);
            if (error is not null) return error;

            var attachment = _attachmentService.Resolve(projectId, request?.AttachmentUrl);
            if (!string.IsNullOrWhiteSpace(request?.AttachmentUrl) && attachment is null) return BadRequest(new { Message = "Pièce jointe introuvable." });

            var isOwner = string.Equals(memory!.OwnerId, CurrentUserId(), StringComparison.Ordinal);
            ClientMessageDto message;
            try
            {
                // Texte assaini (anti-XSS), type validé (sticker autorisé, note vocale réelle…).
                message = ProjectMessaging.Create(!isOwner, isOwner ? CurrentUserDisplayName() : TeamName, request?.MessageType, request?.Content, attachment, request?.AudioDurationSeconds);
            }
            catch (ArgumentException exception)
            {
                return BadRequest(new { Message = exception.Message });
            }

            await ProjectCommunicationHub.AppendMessageAsync(ProjectMemoryService, projectId, message, HttpContext.RequestAborted);
            await _hubContext.Clients.Group(ProjectCommunicationHub.GroupName(projectId)).SendAsync("ReceiveProjectMessage", message, HttpContext.RequestAborted);
            await ProjectCommunicationHub.NotifyAgencyMessageAsync(_hubContext.Clients, projectId, message);
            return Utf8Json(message);
        }

        // POST: api/Projects/{projectId}/attachments  (bouton « + » du chat)
        [HttpPost("{projectId}/attachments")]
        [RequestSizeLimit(11 * 1024 * 1024)]
        public async Task<IActionResult> UploadAttachment(string projectId, IFormFile? file)
        {
            var (_, error) = await ResolveAccessAsync(projectId);
            if (error is not null) return error;
            if (file is null || file.Length == 0) return BadRequest(new { Message = "Aucun fichier reçu." });
            if (file.Length > _attachmentService.MaxFileBytes) return BadRequest(new { Message = "Le fichier dépasse 10 Mo." });
            if (!_attachmentService.IsAllowed(file.FileName)) return BadRequest(new { Message = "Type de fichier non autorisé (images, PDF, Office, TXT, CSV ou ZIP)." });

            await using var stream = file.OpenReadStream();
            var attachment = await _attachmentService.SaveAsync(projectId, stream, file.FileName, HttpContext.RequestAborted);
            return Utf8Json(new { url = attachment.Url, name = attachment.OriginalName, contentType = attachment.ContentType, size = attachment.Size });
        }

        // GET: api/Projects/{projectId}/attachments/{fileName}
        [HttpGet("{projectId}/attachments/{fileName}")]
        public async Task<IActionResult> DownloadAttachment(string projectId, string fileName)
        {
            var (_, error) = await ResolveAccessAsync(projectId);
            if (error is not null) return error;

            var found = _attachmentService.Find(projectId, fileName);
            if (found is null) return NotFound(new { Message = "Pièce jointe introuvable." });

            var (path, attachment) = found.Value;
            // nosniff : le navigateur respecte le type déclaré (pas d'exécution d'un fichier détourné).
            Response.Headers.XContentTypeOptions = "nosniff";
            return PhysicalFile(path, attachment.ContentType, attachment.OriginalName, enableRangeProcessing: true);
        }

        // POST: api/Projects/{projectId}/voice-notes  (bouton micro du chat — propriétaire du fil ou Admin)
        [HttpPost("{projectId}/voice-notes")]
        [RequestSizeLimit(6 * 1024 * 1024)]
        public async Task<IActionResult> UploadVoiceNote(string projectId, IFormFile? file)
        {
            var (_, error) = await ResolveAccessAsync(projectId);
            if (error is not null) return error;
            if (file is null || file.Length == 0) return BadRequest(new { Message = "Aucun enregistrement reçu." });
            if (file.Length > _attachmentService.MaxVoiceNoteBytes) return BadRequest(new { Message = "La note vocale dépasse 5 Mo." });

            try
            {
                await using var stream = file.OpenReadStream();
                // Type déclaré normalisé (« audio/webm;codecs=opus » → « audio/webm »), puis format réel
                // détecté par magic bytes (WebM, Ogg, MP4/M4A, MP3, WAV) ; nom de fichier GUID.
                var contentType = Business.Services.ProjectAttachmentService.NormalizeContentType(file.ContentType);
                var voiceNote = await _attachmentService.SaveVoiceNoteAsync(projectId, stream, contentType, HttpContext.RequestAborted);
                return Utf8Json(new { url = voiceNote.Url, name = voiceNote.OriginalName, contentType = voiceNote.ContentType, size = voiceNote.Size });
            }
            catch (Business.Services.InvalidAudioSignatureException exception)
            {
                Logger.LogWarning("Note vocale refusée sur {ProjectId} : type déclaré {DeclaredType}, {Length} octets, en-tête {HeaderHex}.",
                    projectId, exception.DeclaredType, exception.Length, exception.HeaderHex);
                return BadRequest(new { Message = exception.Message });
            }
            catch (ArgumentException exception)
            {
                return BadRequest(new { Message = exception.Message });
            }
        }

        // POST: api/Projects/{projectId}/progress  (équipe IsMoc uniquement)
        // Fait avancer l'étape (3 → 5) et/ou publie un jalon daté dans la timeline client, diffusé en temps réel.
        [HttpPost("{projectId}/progress")]
        [Authorize(Roles = "Admin")]
        public async Task<IActionResult> AddProgress(string projectId, [FromBody] ProjectProgressRequestDto request)
        {
            if (request is null) return BadRequest(new { Message = "Avancée invalide." });
            var title = ProjectMessaging.Sanitize(request.Title, 200);
            var description = ProjectMessaging.Sanitize(request.Description, 1000);
            if (request.Step is null && string.IsNullOrWhiteSpace(title)) return BadRequest(new { Message = "Indiquez une nouvelle étape ou un titre d'avancée." });
            if (title?.Length > 120) return BadRequest(new { Message = "Le titre ne doit pas dépasser 120 caractères." });
            if (description.Length > 500) return BadRequest(new { Message = "La description ne doit pas dépasser 500 caractères." });

            string? newStatus = null;
            if (request.Step is int step)
            {
                newStatus = ProjectProgress.StatusForStep(step);
                if (newStatus is null) return BadRequest(new { Message = "Les étapes 1 (brief) et 2 (paiement) sont validées par le client : choisissez 3, 4 ou 5." });
            }

            var memory = await GetAccessibleMemoryAsync(projectId, allowAdmin: true);
            if (memory is null) return NotFound(new { Message = "Projet introuvable." });
            if (IsGeneralThread(memory)) return GeneralThreadNotSupported();
            if (newStatus is not null && !ProjectProgress.IsPaid(memory.Status))
            {
                return Conflict(new { Message = "Ce projet n'est pas encore payé : son étape ne peut pas avancer." });
            }

            var milestone = new ProjectMilestoneDto
            {
                Title = string.IsNullOrWhiteSpace(title) ? ProjectProgress.DefaultMilestoneTitle(request.Step ?? 0) : title,
                Description = description,
                Date = DateTimeOffset.UtcNow
            };
            var message = new ClientMessageDto
            {
                Author = "team",
                AuthorName = TeamName,
                Content = string.IsNullOrWhiteSpace(description) ? $"📍 Nouvelle avancée : {milestone.Title}" : $"📍 Nouvelle avancée : {milestone.Title}\n{description}"
            };

            var updated = await ProjectMemoryService.UpdateAsync(projectId, current =>
            {
                if (newStatus is not null) current.Status = newStatus;
                current.Milestones.Add(milestone);
                current.ClientMessages.Add(message);
            }, HttpContext.RequestAborted);
            if (updated is null) return NotFound(new { Message = "Projet introuvable." });

            var payload = new { status = updated.Status, step = ProjectProgress.StepOf(updated.Status), milestones = updated.Milestones };
            var group = _hubContext.Clients.Group(ProjectCommunicationHub.GroupName(projectId));
            await group.SendAsync("ProjectUpdated", payload, HttpContext.RequestAborted);
            await group.SendAsync("ReceiveProjectMessage", message, HttpContext.RequestAborted);
            await ProjectCommunicationHub.NotifyAgencyMessageAsync(_hubContext.Clients, projectId, message);
            return Utf8Json(payload);
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private static string DisplayName(ProjectMemoryDto memory) =>
            string.IsNullOrWhiteSpace(memory.ProjectName) ? "Votre projet" : memory.ProjectName;

        private static ProjectOrderDto BuildOrder(ProjectMemoryDto memory, string plan, ProjectOrderDto? previous)
        {
            var quote = memory.Quote!;
            var (maintenance, period) = ProjectPricing.Maintenance(quote, plan);
            var total = ProjectPricing.ProjectTotal(quote);
            return new ProjectOrderDto
            {
                InvoiceNumber = previous?.InvoiceNumber is { Length: > 0 } number
                    ? number
                    : $"ISM-{DateTime.UtcNow:yyyyMMdd}-{memory.ProjectId[..6].ToUpperInvariant()}",
                MaintenancePlan = plan,
                MaintenanceAmountEur = maintenance,
                MaintenancePeriod = period,
                DevelopmentPriceEur = quote.DevelopmentPriceEur,
                DesignIdentityPriceEur = quote.DesignIdentityPriceEur,
                ProjectTotalEur = total,
                Installments = ProjectPricing.Installments(total),
                OrderedAt = previous?.OrderedAt ?? DateTimeOffset.UtcNow
            };
        }

        private static object CheckoutResponse(ProjectMemoryDto memory, string selectedPlan)
        {
            var quote = memory.Quote;
            var monthly = quote?.MonthlyMaintenanceEur ?? 0;
            var annual = ProjectPricing.AnnualPrice(monthly);
            var total = quote is null ? 0m : ProjectPricing.ProjectTotal(quote);

            return new
            {
                projectId = memory.ProjectId,
                projectName = DisplayName(memory),
                format = memory.Format,
                status = memory.Status,
                currency = "EUR",
                invoiceNumber = memory.Order?.InvoiceNumber,
                complexityLevel = quote?.ComplexityLevel,
                estimatedDays = quote?.EstimatedDays,
                developmentPriceEur = quote?.DevelopmentPriceEur ?? 0,
                designIdentityPriceEur = quote?.DesignIdentityPriceEur ?? 0,
                projectTotalEur = total,
                selectedPlan,
                maintenancePlans = new object[]
                {
                    new { id = "none", label = "Sans", amountEur = 0m, period = (string?)null, discountPercent = 0 },
                    new { id = "monthly", label = "Mensuel", amountEur = (decimal)monthly, period = (string?)"mois", discountPercent = 0 },
                    new { id = "annual", label = "Annuel", amountEur = annual, period = (string?)"an", discountPercent = (int)(ProjectPricing.AnnualMaintenanceDiscount * 100) }
                },
                installments = memory.Order?.Installments ?? ProjectPricing.Installments(total)
            };
        }
    }
}
