using System.Collections.Frozen;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Server.Realtime.Actors;

/// <summary>
/// Principal → connections (each with its tab) for one room. A principal stays present for the grace period after
/// its last connection closes, so a reconnect causes no leave/join flicker; the tabs listed are only the connected
/// ones, which is what spotlight binding needs (R7). Grace expiries are generation-checked: the actor arms a timer
/// with the generation returned here and passes it back when the timer fires.
/// </summary>
public sealed class PresenceTable {
    private readonly Dictionary<PrincipalId, Entry> _entries = [];
    private PresenceView? _view;
    private IReadOnlyList<PrincipalInfo>? _infos;
    private long _generation;

    public PresenceView View => _view ??= BuildView();

    /// <summary>Present principals (grace included) in canonical order: pid as lower-case string, ordinal.</summary>
    public IReadOnlyList<PrincipalInfo> Infos => _infos ??= [.. _entries.Values
        .Select(x => x.Info)
        .OrderBy(x => x.Pid.Value.ToString("D"), StringComparer.Ordinal)];

    /// <summary>Every present principal with the snapshot variant it last subscribed with.</summary>
    public IEnumerable<Audience> Audiences => _entries.Values.Select(x => new Audience(x.Info.Pid, x.Member));

    public bool IsPresent(PrincipalId principal) => _entries.ContainsKey(principal);

    public int ConnectionsOf(PrincipalId principal) {
        return _entries.TryGetValue(principal, out var entry) ? entry.Connections.Count : 0;
    }

    /// <summary>
    /// Adds a connection. <see cref="PresenceChange.Joined"/> when the principal was absent; a reconnect inside the
    /// grace period cancels the pending leave silently and reports <see cref="PresenceChange.ClientConnected"/>.
    /// </summary>
    public PresenceDelta Add(ConnectionId connection, Viewer viewer, bool member) {
        var principal = viewer.Who.Id;
        var info = Info(viewer.Who);
        Changed();
        if (!_entries.TryGetValue(principal, out var entry)) {
            entry = new Entry(info, member);
            entry.Connections[connection] = viewer.Client;
            _entries[principal] = entry;
            return new PresenceDelta(PresenceChange.Joined, principal, viewer.Client);
        }
        entry.Member = member;
        entry.LeaveGeneration = 0;
        entry.Connections[connection] = viewer.Client;
        entry.GoneClients.Remove(viewer.Client);
        return new PresenceDelta(PresenceChange.ClientConnected, principal, viewer.Client);
    }

    /// <summary>
    /// Removes a connection. Returns the generations of the grace timers to arm: one for the principal when this was
    /// its last connection, one for the tab when this was the tab's last connection.
    /// </summary>
    public GraceTimers Remove(ConnectionId connection, PrincipalId principal) {
        if (!_entries.TryGetValue(principal, out var entry) || !entry.Connections.Remove(connection, out var client)) {
            return default;
        }
        Changed();
        long? clientGeneration = null;
        if (!entry.Connections.ContainsValue(client)) {
            clientGeneration = ++_generation;
            entry.GoneClients[client] = clientGeneration.Value;
        }
        long? leaveGeneration = null;
        if (entry.Connections.Count == 0) {
            leaveGeneration = ++_generation;
            entry.LeaveGeneration = leaveGeneration.Value;
        }
        return new GraceTimers(client, leaveGeneration, clientGeneration);
    }

    /// <summary><see cref="PresenceChange.Left"/> when the principal's grace expired without a reconnect.</summary>
    public PresenceDelta? ExpireLeave(PrincipalId principal, long generation) {
        if (!_entries.TryGetValue(principal, out var entry)
            || entry.Connections.Count > 0
            || entry.LeaveGeneration != generation) {
            return null;
        }
        _entries.Remove(principal);
        Changed();
        return new PresenceDelta(PresenceChange.Left, principal);
    }

    /// <summary><see cref="PresenceChange.ClientGone"/> when a tab stayed disconnected for the grace period.</summary>
    public PresenceDelta? ExpireClient(PrincipalId principal, ClientId client, long generation) {
        if (_entries.TryGetValue(principal, out var entry)) {
            if (!entry.GoneClients.TryGetValue(client, out var armed) || armed != generation) {
                return null;
            }
            entry.GoneClients.Remove(client);
        }
        return new PresenceDelta(PresenceChange.ClientGone, principal, client);
    }

    /// <summary><see cref="PresenceChange.Updated"/> when a present principal's name or avatar changed.</summary>
    public PresenceDelta? Update(PrincipalInfo info) {
        if (!_entries.TryGetValue(info.Pid, out var entry) || entry.Info == info) {
            return null;
        }
        entry.Info = info;
        Changed();
        return new PresenceDelta(PresenceChange.Updated, info.Pid);
    }

    public PrincipalInfo? InfoOf(PrincipalId principal) => _entries.TryGetValue(principal, out var entry) ? entry.Info : null;

    public void Clear() {
        _entries.Clear();
        Changed();
    }

    public static PrincipalInfo Info(Principal who) => new(who.Id, who.Kind, who.DisplayName, who.AvatarBlobId);

    private void Changed() {
        _view = null;
        _infos = null;
    }

    private PresenceView BuildView() {
        return new PresenceView(
            _entries.Keys.ToFrozenSet(),
            _entries.ToFrozenDictionary(x => x.Key, x => x.Value.Connections.Values.ToFrozenSet()));
    }

    private sealed class Entry(PrincipalInfo info, bool member) {
        public PrincipalInfo Info { get; set; } = info;

        public bool Member { get; set; } = member;

        public Dictionary<ConnectionId, ClientId> Connections { get; } = [];

        /// <summary>Tabs whose last connection closed, with the generation of their grace timer.</summary>
        public Dictionary<ClientId, long> GoneClients { get; } = [];

        /// <summary>Nonzero while a leave is pending.</summary>
        public long LeaveGeneration { get; set; }
    }
}

/// <param name="Client">The tab of the removed connection.</param>
/// <param name="Leave">Arm the principal's grace timer with this generation.</param>
/// <param name="ClientGone">Arm the tab's grace timer with this generation.</param>
public readonly record struct GraceTimers(ClientId Client, long? Leave, long? ClientGone);
