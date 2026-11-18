using System.Collections.Concurrent;

namespace IsMocStudio.api.Hubs
{
    /// <summary>Appel en cours de sonnerie sur un projet (pour le remettre à un membre qui rejoint la salle ensuite).</summary>
    /// <param name="TeamOffline">Client qui appelle alors que l'équipe est hors ligne : appel manqué enregistré à la fin.</param>
    public sealed record PendingCall(string CallerConnectionId, string CallerName, string CallType, bool CallerIsTeam, DateTimeOffset StartedAt, bool TeamOffline = false);

    /// <summary>
    /// État temps réel en mémoire du serveur : connexions par salle projet, lobby agence (cockpit /admin)
    /// et appels en attente de réponse.
    /// </summary>
    public sealed class ProjectPresenceTracker
    {
        private static readonly TimeSpan RingTimeout = TimeSpan.FromSeconds(60);
        private static readonly TimeSpan MissedCallEmailInterval = TimeSpan.FromMinutes(5);

        // projectId -> (connectionId -> estMembreDeLEquipe)
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, bool>> _rooms = new(StringComparer.Ordinal);

        // connectionId -> projets rejoints
        private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, byte>> _connections = new(StringComparer.Ordinal);

        // Connexions du cockpit agence (comptes Admin).
        private readonly ConcurrentDictionary<string, byte> _agencyLobby = new(StringComparer.Ordinal);

        // projectId -> appel qui sonne
        private readonly ConcurrentDictionary<string, PendingCall> _pendingCalls = new(StringComparer.Ordinal);

        // projectId -> dernier e-mail « appel manqué » (au plus un toutes les 5 minutes par projet)
        private readonly ConcurrentDictionary<string, DateTimeOffset> _missedCallEmails = new(StringComparer.Ordinal);

        public void Join(string projectId, string connectionId, bool isTeam)
        {
            _rooms.GetOrAdd(projectId, _ => new ConcurrentDictionary<string, bool>(StringComparer.Ordinal))[connectionId] = isTeam;
            _connections.GetOrAdd(connectionId, _ => new ConcurrentDictionary<string, byte>(StringComparer.Ordinal))[projectId] = 0;
        }

        public void Leave(string projectId, string connectionId)
        {
            if (_rooms.TryGetValue(projectId, out var room))
            {
                room.TryRemove(connectionId, out _);
                if (room.IsEmpty) _rooms.TryRemove(new KeyValuePair<string, ConcurrentDictionary<string, bool>>(projectId, room));
            }

            if (_connections.TryGetValue(connectionId, out var projects)) projects.TryRemove(projectId, out _);
            if (_pendingCalls.TryGetValue(projectId, out var call) && call.CallerConnectionId == connectionId) _pendingCalls.TryRemove(projectId, out _);
        }

        /// <summary>Retire la connexion de toutes ses salles et du lobby ; retourne les projets concernés.</summary>
        public IReadOnlyList<string> LeaveAll(string connectionId)
        {
            _agencyLobby.TryRemove(connectionId, out _);
            if (!_connections.TryRemove(connectionId, out var projects)) return [];
            foreach (var projectId in projects.Keys) Leave(projectId, connectionId);
            return projects.Keys.ToList();
        }

        public bool IsInRoom(string projectId, string connectionId) =>
            _rooms.TryGetValue(projectId, out var room) && room.ContainsKey(connectionId);

        /// <summary>L'équipe est en ligne si un membre est dans la salle ou si le cockpit agence est ouvert.</summary>
        public bool IsTeamOnline(string projectId) =>
            !_agencyLobby.IsEmpty || (_rooms.TryGetValue(projectId, out var room) && room.Values.Any(isTeam => isTeam));

        public bool IsClientOnline(string projectId) =>
            _rooms.TryGetValue(projectId, out var room) && room.Values.Any(isTeam => !isTeam);

        /// <summary>Projets ayant au moins une connexion (pour diffuser un changement de présence globale).</summary>
        public IReadOnlyList<string> ActiveProjects() => _rooms.Keys.ToList();

        public void JoinLobby(string connectionId) => _agencyLobby[connectionId] = 0;
        public bool LeaveLobby(string connectionId) => _agencyLobby.TryRemove(connectionId, out _);
        public bool IsInLobby(string connectionId) => _agencyLobby.ContainsKey(connectionId);

        public void SetPendingCall(string projectId, PendingCall call) => _pendingCalls[projectId] = call;
        public void ClearPendingCall(string projectId) => _pendingCalls.TryRemove(projectId, out _);

        /// <summary>
        /// Retire et retourne l'appel en attente de ce projet s'il a été lancé par cette connexion alors que l'équipe
        /// était hors ligne (appel manqué à enregistrer) ; sinon null et rien ne change.
        /// </summary>
        public PendingCall? TakeOfflineCall(string projectId, string connectionId)
        {
            if (!_pendingCalls.TryGetValue(projectId, out var call) || !call.TeamOffline || call.CallerConnectionId != connectionId) return null;
            return _pendingCalls.TryRemove(new KeyValuePair<string, PendingCall>(projectId, call)) ? call : null;
        }

        /// <summary>Projets où cette connexion a un appel « équipe hors ligne » en cours (déconnexion pendant la sonnerie).</summary>
        public IReadOnlyList<string> OfflineCallsBy(string connectionId) =>
            _pendingCalls.Where(entry => entry.Value.TeamOffline && entry.Value.CallerConnectionId == connectionId).Select(entry => entry.Key).ToList();

        /// <summary>Vrai (et mémorise l'envoi) si aucun e-mail « appel manqué » n'est parti pour ce projet depuis 5 minutes.</summary>
        public bool TryReserveMissedCallEmail(string projectId)
        {
            var now = DateTimeOffset.UtcNow;
            while (true)
            {
                if (!_missedCallEmails.TryGetValue(projectId, out var last))
                {
                    if (_missedCallEmails.TryAdd(projectId, now)) return true;
                    continue;
                }
                if (now - last < MissedCallEmailInterval) return false;
                if (_missedCallEmails.TryUpdate(projectId, now, last)) return true;
            }
        }

        public PendingCall? GetPendingCall(string projectId)
        {
            if (!_pendingCalls.TryGetValue(projectId, out var call)) return null;
            if (DateTimeOffset.UtcNow - call.StartedAt <= RingTimeout) return call;
            _pendingCalls.TryRemove(projectId, out _);
            return null;
        }
    }
}
