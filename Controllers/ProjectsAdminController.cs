using System.Globalization;
using IsMocStudio.Business.Helpers;
using IsMocStudio.Core.DTOs;
using IsMocStudio.Core.Entities;
using IsMocStudio.Core.Interfaces.Repositories;
using IsMocStudio.Core.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace IsMocStudio.api.Controllers
{
    /// <summary>
    /// Back-office agence (cockpit /admin) : vue globale des commandes, projets et clients.
    /// Mêmes URL que ProjectsController (api/Projects/admin/…), dans un contrôleur dédié réservé aux Admin.
    /// </summary>
    [Route("api/Projects/admin")]
    [ApiController]
    [Authorize(Roles = "Admin")]
    public class ProjectsAdminController : ControllerBase
    {
        private const int MessagePreviewLength = 160;

        private readonly IProjectMemoryService _memoryService;
        private readonly IProjectDeliverableService _deliverableService;
        private readonly IUnitOfWork _unitOfWork;

        public ProjectsAdminController(IProjectMemoryService memoryService, IProjectDeliverableService deliverableService, IUnitOfWork unitOfWork)
        {
            _memoryService = memoryService;
            _deliverableService = deliverableService;
            _unitOfWork = unitOfWork;
        }

        // GET: api/Projects/admin/overview
        [HttpGet("overview")]
        public async Task<IActionResult> Overview()
        {
            var users = await LoadUsersAsync();

            // Chaque client dispose d'un fil « Discussion générale · Avant-projet » : l'agence peut lui écrire
            // ou l'appeler dès son inscription, même sans maquette.
            foreach (var user in users.Values.Where(user => !string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase)))
            {
                await _memoryService.EnsureGeneralThreadAsync(user.Id.ToString(CultureInfo.InvariantCulture), HttpContext.RequestAborted);
            }

            var allThreads = await _memoryService.ListAllAsync(HttpContext.RequestAborted);
            // Les statistiques ne portent que sur les vrais projets (pas sur les fils de discussion généraux).
            var memories = allThreads.Where(memory => memory.ThreadType != ProjectThreadTypes.General).ToList();

            var paidMemories = memories.Where(memory => ProjectProgress.IsPaid(memory.Status) && memory.Order is not null).ToList();
            var stats = new
            {
                TotalRevenueEur = paidMemories.Sum(memory => PaidAmount(memory.Order!)),
                PendingInstallmentsEur = paidMemories.Sum(memory => memory.Order!.ProjectTotalEur - PaidAmount(memory.Order!)),
                ActiveProjectsCount = memories.Count(memory => memory.Status == ProjectStatuses.InDevelopment),
                DeliveredProjectsCount = memories.Count(memory => memory.Status is ProjectStatuses.Delivered or ProjectStatuses.Maintenance),
                AwaitingPaymentCount = memories.Count(memory => memory.Status == ProjectStatuses.Ordered),
                TotalProjectsCount = memories.Count,
                TotalClientsCount = users.Values.Count(user => !string.Equals(user.Role, "Admin", StringComparison.OrdinalIgnoreCase))
            };

            var projects = allThreads
                .Select(memory => new { memory, lastActivity = LastActivity(memory) })
                .OrderByDescending(item => item.lastActivity)
                .Select(item => ProjectOverview(item.memory, item.lastActivity, users))
                .ToList();

            return Utf8Json(new { stats, projects });
        }

        // GET: api/Projects/admin/clients
        [HttpGet("clients")]
        public async Task<IActionResult> Clients()
        {
            var memories = (await _memoryService.ListAllAsync(HttpContext.RequestAborted))
                .Where(memory => memory.ThreadType != ProjectThreadTypes.General)
                .ToList();
            var byOwner = memories.ToLookup(memory => memory.OwnerId, StringComparer.Ordinal);
            var users = await LoadUsersAsync();

            var clients = users.Values
                .Select(user =>
                {
                    var projects = byOwner[user.Id.ToString(CultureInfo.InvariantCulture)].ToList();
                    var paid = projects.Where(memory => ProjectProgress.IsPaid(memory.Status) && memory.Order is not null).ToList();
                    return new
                    {
                        ClientId = user.Id,
                        FullName = $"{user.FirstName} {user.LastName}".Trim(),
                        user.Email,
                        user.PhoneNumber,
                        user.Role,
                        RegisteredAt = user.CreatedAt,
                        ProjectsCount = projects.Count,
                        PaidProjectsCount = paid.Count,
                        RevenueEur = paid.Sum(memory => PaidAmount(memory.Order!)),
                        CommittedEur = paid.Sum(memory => memory.Order!.ProjectTotalEur),
                        LastActivityAt = projects.Count == 0 ? (DateTimeOffset?)null : projects.Max(LastActivity)
                    };
                })
                .OrderByDescending(client => client.RevenueEur)
                .ThenByDescending(client => client.RegisteredAt)
                .ToList();

            return Utf8Json(clients);
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private async Task<Dictionary<string, User>> LoadUsersAsync()
        {
            var users = await _unitOfWork.Users.GetAllAsync();
            return users.ToDictionary(user => user.Id.ToString(CultureInfo.InvariantCulture), StringComparer.Ordinal);
        }

        private static decimal PaidAmount(ProjectOrderDto order) =>
            order.Installments.Where(installment => installment.Status == "paid").Sum(installment => installment.AmountEur);

        private static DateTimeOffset LastActivity(ProjectMemoryDto memory)
        {
            var lastMessage = memory.ClientMessages.Count == 0 ? DateTimeOffset.MinValue : memory.ClientMessages.Max(message => message.SentAt);
            return lastMessage > memory.UpdatedAt ? lastMessage : memory.UpdatedAt;
        }

        private object ProjectOverview(ProjectMemoryDto memory, DateTimeOffset lastActivity, IReadOnlyDictionary<string, User> users)
        {
            users.TryGetValue(memory.OwnerId, out var owner);
            var order = memory.Order;
            var lastMessage = memory.ClientMessages.OrderBy(message => message.SentAt).LastOrDefault();
            var step = ProjectProgress.StepOf(memory.Status);

            return new
            {
                memory.ProjectId,
                memory.ThreadType,
                Reference = $"ISM-{memory.ProjectId[..4].ToUpperInvariant()}",
                Title = string.IsNullOrWhiteSpace(memory.ProjectName) ? "Projet sans nom" : memory.ProjectName,
                Type = memory.Format == "mobile" ? "Mobile" : "Web",
                memory.Format,
                memory.Status,
                PaymentStatus = ProjectProgress.PaymentStatus(memory.Status),
                Step = step,
                StepLabel = ProjectProgress.StepLabels[step - 1],
                memory.InitialIdea,
                memory.CreatedAt,
                LastActivityAt = lastActivity,
                HasDeliverable = memory.Status != ProjectStatuses.Draft && _deliverableService.Exists(memory.ProjectId),
                Order = order is null ? null : new
                {
                    order.InvoiceNumber,
                    TotalEur = order.ProjectTotalEur,
                    PaidEur = PaidAmount(order),
                    PendingEur = order.ProjectTotalEur - PaidAmount(order),
                    order.MaintenancePlan,
                    order.MaintenanceAmountEur,
                    order.MaintenancePeriod,
                    order.PaymentMethod,
                    order.PaymentDetail,
                    order.CardLast4,
                    order.OrderedAt,
                    order.PaidAt,
                    order.Installments
                },
                RecentMilestones = memory.Milestones.OrderByDescending(milestone => milestone.Date).Take(3).ToList(),
                LastMessage = lastMessage is null ? null : new
                {
                    lastMessage.Author,
                    lastMessage.AuthorName,
                    lastMessage.MessageType,
                    Content = ProjectMessaging.Preview(lastMessage, MessagePreviewLength),
                    HasAttachment = lastMessage.AttachmentUrl is not null,
                    lastMessage.SentAt
                },
                MessagesCount = memory.ClientMessages.Count,
                Client = new
                {
                    ClientId = owner?.Id,
                    FullName = owner is null ? "Client supprimé" : $"{owner.FirstName} {owner.LastName}".Trim(),
                    Email = owner?.Email ?? string.Empty,
                    PhoneNumber = owner?.PhoneNumber ?? string.Empty
                }
            };
        }

        private static JsonResult Utf8Json(object value) => new(value) { ContentType = "application/json; charset=utf-8" };
    }
}
