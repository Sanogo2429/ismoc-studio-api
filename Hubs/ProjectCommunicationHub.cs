using System.Security.Claims;
using IsMocStudio.Business.Helpers;
using IsMocStudio.Core.DTOs;
using IsMocStudio.Core.Interfaces.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;

namespace IsMocStudio.api.Hubs
{
    /// <summary>
    /// Communication temps réel (route /hubs/project-communication) :
    /// salles par projet, présence équipe/client, messagerie persistée, signalisation WebRTC (audio/vidéo)
    /// et lobby agence (cockpit /admin) notifié de tous les messages et appels entrants.
    /// </summary>
    [Authorize]
    public sealed class ProjectCommunicationHub : Hub
    {
        public const string Route = "/hubs/project-communication";
        public const string TeamName = "Équipe IsMoc";
        public const string AgencyGroup = "agency-lobby";
        private const int MaxSignalLength = 64 * 1024;

        private readonly IProjectMemoryService _memoryService;
        private readonly IProjectAttachmentService _attachmentService;
        private readonly ProjectPresenceTracker _presence;
        private readonly ILogger<ProjectCommunicationHub> _logger;

        public ProjectCommunicationHub(
            IProjectMemoryService memoryService,
            IProjectAttachmentService attachmentService,
            ProjectPresenceTracker presence,
            ILogger<ProjectCommunicationHub> logger)
        {
            _memoryService = memoryService;
            _attachmentService = attachmentService;
            _presence = presence;
            _logger = logger;
        }

        public static string GroupName(string projectId) => $"project:{projectId}";

        /// <summary>Diffuse la présence de l'équipe (PresenceUpdated) et du client (ClientPresenceUpdated) dans la salle.</summary>
        public static async Task BroadcastPresenceAsync(IHubClients<IClientProxy> clients, ProjectPresenceTracker presence, string projectId)
        {
            var group = clients.Group(GroupName(projectId));
            await group.SendAsync("PresenceUpdated", presence.IsTeamOnline(projectId));
            await group.SendAsync("ClientPresenceUpdated", presence.IsClientOnline(projectId));
        }

        /// <summary>Notifie le cockpit agence d'un nouveau message (aperçu dans la liste des dossiers).</summary>
        public static Task NotifyAgencyMessageAsync(IHubClients<IClientProxy> clients, string projectId, ClientMessageDto message) =>
            clients.Group(AgencyGroup).SendAsync("AgencyNewMessage", projectId, message);

        // ------------------------------------------------------------------
        // Salles et présence
        // ------------------------------------------------------------------

        /// <summary>Rejoint la salle du projet et retourne la présence actuelle de l'équipe.</summary>
        public async Task<bool> JoinProjectRoom(string projectId)
        {
            var memory = await GetAccessibleMemoryAsync(projectId);
            var isTeam = IsTeamMember(memory);

            await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(projectId));
            _presence.Join(projectId, Context.ConnectionId, isTeam);
            await BroadcastPresenceAsync(Clients, _presence, projectId);

            // Un appel sonne déjà côté interlocuteur : on le présente à la personne qui vient d'arriver.
            var pending = _presence.GetPendingCall(projectId);
            if (pending is not null && pending.CallerConnectionId != Context.ConnectionId && pending.CallerIsTeam != isTeam)
            {
                await Clients.Caller.SendAsync("IncomingCall", pending.CallerName, pending.CallType);
            }

            return _presence.IsTeamOnline(projectId);
        }

        public async Task LeaveProjectRoom(string projectId)
        {
            if (!_presence.IsInRoom(projectId, Context.ConnectionId)) return;
            _presence.Leave(projectId, Context.ConnectionId);
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(projectId));
            await Clients.OthersInGroup(GroupName(projectId)).SendAsync("CallEnded");
            await BroadcastPresenceAsync(Clients, _presence, projectId);
        }

        /// <summary>Cockpit agence : reçoit AgencyNewMessage / AgencyIncomingCall de tous les projets.</summary>
        public async Task JoinAgencyLobby()
        {
            if (Context.User?.IsInRole("Admin") != true) throw new HubException("Accès réservé à l'équipe IsMoc.");
            await Groups.AddToGroupAsync(Context.ConnectionId, AgencyGroup);
            _presence.JoinLobby(Context.ConnectionId);
            foreach (var projectId in _presence.ActiveProjects()) await BroadcastPresenceAsync(Clients, _presence, projectId);
        }

        public async Task LeaveAgencyLobby()
        {
            if (!_presence.LeaveLobby(Context.ConnectionId)) return;
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, AgencyGroup);
            foreach (var projectId in _presence.ActiveProjects()) await BroadcastPresenceAsync(Clients, _presence, projectId);
        }

        public override async Task OnDisconnectedAsync(Exception? exception)
        {
            var wasInLobby = _presence.IsInLobby(Context.ConnectionId);
            var leftProjects = _presence.LeaveAll(Context.ConnectionId);
            var toRefresh = wasInLobby ? _presence.ActiveProjects().Union(leftProjects) : leftProjects;
            foreach (var projectId in toRefresh)
            {
                if (leftProjects.Contains(projectId)) await Clients.Group(GroupName(projectId)).SendAsync("CallEnded");
                await BroadcastPresenceAsync(Clients, _presence, projectId);
            }

            await base.OnDisconnectedAsync(exception);
        }

        // ------------------------------------------------------------------
        // Messagerie
        // ------------------------------------------------------------------

        /// <summary>Message texte (avec pièce jointe éventuelle).</summary>
        public Task<ClientMessageDto> SendProjectMessage(string projectId, string? content, string? attachmentUrl) =>
            SendTypedProjectMessage(projectId, ClientMessageTypes.Text, content, attachmentUrl, null);

        /// <summary>
        /// Message typé façon messagerie mobile : "text" | "sticker" (content = identifiant du sticker) | "voice"
        /// (attachmentUrl = note vocale de /voice-notes). Contenu assaini contre le XSS ; types image/fichier déduits.
        /// </summary>
        public async Task<ClientMessageDto> SendTypedProjectMessage(string projectId, string? messageType, string? content, string? attachmentUrl, int? audioDurationSeconds)
        {
            EnsureJoined(projectId);
            var memory = await GetAccessibleMemoryAsync(projectId);

            var attachment = _attachmentService.Resolve(projectId, attachmentUrl);
            if (!string.IsNullOrWhiteSpace(attachmentUrl) && attachment is null) throw new HubException("Pièce jointe introuvable.");

            var isTeam = IsTeamMember(memory);
            ClientMessageDto message;
            try
            {
                message = ProjectMessaging.Create(isTeam, isTeam ? TeamName : DisplayName(), messageType, content, attachment, audioDurationSeconds);
            }
            catch (ArgumentException exception)
            {
                throw new HubException(exception.Message);
            }

            await AppendMessageAsync(_memoryService, projectId, message, Context.ConnectionAborted);
            await Clients.Group(GroupName(projectId)).SendAsync("ReceiveProjectMessage", message);
            await NotifyAgencyMessageAsync(Clients, projectId, message);
            return message;
        }

        /// <summary>Ajout atomique d'un message dans App_Data/Memories/{projectId}.json.</summary>
        public static async Task AppendMessageAsync(IProjectMemoryService memoryService, string projectId, ClientMessageDto message, CancellationToken cancellationToken)
        {
            var updated = await memoryService.UpdateAsync(projectId, memory => memory.ClientMessages.Add(message), cancellationToken);
            if (updated is null) throw new HubException("Projet introuvable.");
        }

        // ------------------------------------------------------------------
        // Appels audio / vidéo : signalisation WebRTC entre pairs
        // ------------------------------------------------------------------

        public async Task InitiateCall(string projectId, string callType)
        {
            EnsureJoined(projectId);
            if (callType is not ("audio" or "video")) throw new HubException("Type d'appel invalide (audio ou video).");

            var memory = await GetAccessibleMemoryAsync(projectId);
            var isTeam = IsTeamMember(memory);
            var callerName = isTeam ? TeamName : DisplayName();
            _presence.SetPendingCall(projectId, new PendingCall(Context.ConnectionId, callerName, callType, isTeam, DateTimeOffset.UtcNow));

            _logger.LogInformation("Appel {CallType} lancé sur le projet {ProjectId}.", callType, projectId);
            await Clients.OthersInGroup(GroupName(projectId)).SendAsync("IncomingCall", callerName, callType);

            // Un client appelle : tout le cockpit agence est prévenu, même sans la conversation ouverte.
            if (!isTeam) await Clients.Group(AgencyGroup).SendAsync("AgencyIncomingCall", projectId, callerName, callType);
        }

        public async Task AcceptCall(string projectId)
        {
            EnsureJoined(projectId);
            _presence.ClearPendingCall(projectId);
            await Clients.OthersInGroup(GroupName(projectId)).SendAsync("CallAccepted");
            await Clients.Group(AgencyGroup).SendAsync("AgencyCallEnded", projectId);
        }

        public async Task RejectOrEndCall(string projectId)
        {
            EnsureJoined(projectId);
            _presence.ClearPendingCall(projectId);
            await Clients.OthersInGroup(GroupName(projectId)).SendAsync("CallEnded");
            await Clients.Group(AgencyGroup).SendAsync("AgencyCallEnded", projectId);
        }

        public Task SendWebRtcOffer(string projectId, string sdp)
        {
            EnsureJoined(projectId);
            EnsureSignalSize(sdp);
            return Clients.OthersInGroup(GroupName(projectId)).SendAsync("ReceiveWebRtcOffer", sdp);
        }

        public Task SendWebRtcAnswer(string projectId, string sdp)
        {
            EnsureJoined(projectId);
            EnsureSignalSize(sdp);
            return Clients.OthersInGroup(GroupName(projectId)).SendAsync("ReceiveWebRtcAnswer", sdp);
        }

        public Task SendIceCandidate(string projectId, string candidateJson)
        {
            EnsureJoined(projectId);
            EnsureSignalSize(candidateJson);
            return Clients.OthersInGroup(GroupName(projectId)).SendAsync("ReceiveIceCandidate", candidateJson);
        }

        // ------------------------------------------------------------------
        // Helpers
        // ------------------------------------------------------------------

        private string? UserId() =>
            Context.User?.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? Context.User?.FindFirst("sub")?.Value;

        private string DisplayName()
        {
            var fullName = $"{Context.User?.FindFirst(ClaimTypes.GivenName)?.Value} {Context.User?.FindFirst(ClaimTypes.Surname)?.Value}".Trim();
            return string.IsNullOrWhiteSpace(fullName) ? "Client" : fullName;
        }

        /// <summary>Un administrateur qui n'est pas le propriétaire du projet représente l'équipe IsMoc.</summary>
        private bool IsTeamMember(ProjectMemoryDto memory) =>
            Context.User?.IsInRole("Admin") == true && !string.Equals(memory.OwnerId, UserId(), StringComparison.Ordinal);

        /// <summary>Propriétaire du fil ou Admin uniquement ; sinon HubException « Accès refusé » (équivalent 403).</summary>
        private async Task<ProjectMemoryDto> GetAccessibleMemoryAsync(string projectId)
        {
            if (!Guid.TryParseExact(projectId, "N", out _)) throw new HubException("Identifiant de projet invalide.");
            var userId = UserId();
            if (string.IsNullOrWhiteSpace(userId)) throw new HubException("Accès refusé.");

            var memory = await _memoryService.GetAsync(projectId, Context.ConnectionAborted) ?? throw new HubException("Projet introuvable.");
            if (!string.Equals(memory.OwnerId, userId, StringComparison.Ordinal) && Context.User?.IsInRole("Admin") != true)
            {
                _logger.LogWarning("Accès refusé au fil {ProjectId} pour l'utilisateur {UserId}.", projectId, userId);
                throw new HubException("Accès refusé : ce fil de discussion ne vous appartient pas.");
            }

            return memory;
        }

        private void EnsureJoined(string projectId)
        {
            if (!_presence.IsInRoom(projectId, Context.ConnectionId)) throw new HubException("Rejoignez d'abord la salle du projet.");
        }

        private static void EnsureSignalSize(string? value)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > MaxSignalLength) throw new HubException("Signal WebRTC invalide.");
        }
    }
}
