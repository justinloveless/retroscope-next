using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Retroscope.Core.Authz;
using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Server.Realtime.Actors;
using Retroscope.Server.Realtime.Stores;
using Retroscope.Server.Realtime.Transport;

namespace Retroscope.Server.Realtime.Rooms;

/// <summary>
/// The poker room (§c.4): the real <see cref="PokerDomain"/> on the actor runtime, persisted by
/// <see cref="PokerStore"/>, with Jira reached only through <see cref="IPokerJiraPort"/> (§f).
/// </summary>
public static class PokerRoom {
    public static void AddServices(IServiceCollection services, IConfiguration configuration) {
        // Deny-all until the team room registers the membership-backed view (U23); a later registration wins.
        services.TryAddSingleton<IAccessView, NoTeamAccess>();
        services.AddSingleton<RoomGateway>();
        services.Replace(ServiceDescriptor.Singleton<IRoomGateway>(sp => sp.GetRequiredService<RoomGateway>()));
        services.AddSingleton(sp => new PokerJiraLoop(
            sp.GetService<IPokerJiraPort>(),
            sp.GetRequiredService<TimeProvider>(),
            sp.GetRequiredService<ActorRegistry>,
            sp.GetRequiredService<RoomGateway>,
            sp.GetRequiredService<ILoggerFactory>().CreateLogger<PokerJiraLoop>()));
        services.AddSingleton<IResumeFill>(sp => sp.GetRequiredService<PokerJiraLoop>());
        services.AddSingleton<PokerStore>();
        services.AddRoom(
            sp => new PokerBinding(new PokerRoomDomain(
                new PokerDomain(),
                sp.GetRequiredService<PokerJiraLoop>(),
                sp.GetService<ITeamNotices>())),
            sp => sp.GetRequiredService<PokerStore>());
    }

    public static void Map(IEndpointRouteBuilder app) {
    }
}

/// <summary>How the runtime runs a poker room (U13's <see cref="IRoomBinding{TState,TInput,TEvent}"/>).</summary>
public sealed class PokerBinding(IRoomDomain<PokerState, PokerInput, PokerEvent> domain)
    : IRoomBinding<PokerState, PokerInput, PokerEvent> {
    public RoomKind Kind => RoomKind.Poker;

    public IRoomDomain<PokerState, PokerInput, PokerEvent> Domain { get; } = domain;

    public PokerInput? FromClient(Command command) => command is PokerCommand poker ? (PokerInput)poker : null;

    public EventDurability DurabilityOf(PokerEvent evt) => evt.Durability;

    public bool CanSubscribe(PokerState state, Viewer viewer, IAccessView access) {
        return Policy.Can(viewer, Capability.Subscribe, Facts(state, viewer), access);
    }

    public RoomSnapshot Snapshot(PokerState state, IReadOnlyList<PrincipalInfo> presence, bool forMembers) {
        return PokerProjection.Snapshot(state, presence);
    }

    public RoomOverlay? Overlay(PokerState state, Viewer viewer) => PokerProjection.Overlay(state, viewer.Who.Id, viewer.Client);

    public IReadOnlyList<WireEvent> Project(
        PokerState state,
        IReadOnlyList<PokerEvent> events,
        PrincipalId viewer,
        bool viewerIsMember) {
        return PokerProjection.Events(events, viewer);
    }

    public bool Supersedes(IReadOnlyList<PokerEvent> latest, IReadOnlyList<PokerEvent> previous) {
        return latest.All(x => x is SpotlightMoved) && previous.All(x => x is SpotlightMoved);
    }

    public DateTimeOffset? WarmUntil(PokerState state) => state.WarmUntil;

    public bool IsDeleted(PokerState state) => state.Deleted;

    public PokerInput? OnShutdown(PokerState state) => state.Spotlight is { Dirty: true } ? new SpotlightSettleDue() : null;

    private static ResourceFacts Facts(PokerState state, Viewer viewer) {
        return new ResourceFacts(
            RoomKey.Poker(state.SessionId),
            Team: state.TeamId,
            CreatedBy: state.CreatedBy,
            Deleted: state.Deleted,
            IsObserver: state.Observers.Contains(viewer.Who.Id),
            JiraConfigured: state.Jira.Configured,
            MembersCanEditJira: state.Jira.MembersCanEdit);
    }
}

/// <summary>
/// <see cref="PokerDomain"/> plus the room's effects outside the actor: a pending Jira submit starts the off-actor
/// write, the meta loop follows the ticket keys, and the team page hears when the session's tallies change.
/// </summary>
public sealed class PokerRoomDomain(
    IRoomDomain<PokerState, PokerInput, PokerEvent> inner,
    PokerJiraLoop jira,
    ITeamNotices? notices) : IRoomDomain<PokerState, PokerInput, PokerEvent> {
    private readonly ConditionalWeakTable<PokerState, StrongBox<(int Pointed, int Total)>> _tallies = [];

    public Decision<PokerEvent> Decide(PokerState state, PokerInput command, CommandContext context) {
        return inner.Decide(state, command, context);
    }

    public void Apply(PokerState state, PokerEvent evt) {
        inner.Apply(state, evt);
        if (evt is JiraSubmitChanged { Status: JiraSubmitStatus.Pending } submit && state.TeamId is { } team) {
            jira.Submit(RoomKey.Poker(state.SessionId), team, submit);
        }
    }

    public IReadOnlyList<PokerEvent> OnPresence(PokerState state, PresenceDelta delta, PresenceView presence, DateTimeOffset now) {
        return inner.OnPresence(state, delta, presence, now);
    }

    // The runtime asks for timers after every message it handled, on the actor's thread, so this is where the room
    // notices a change of its ticket keys or tallies, whatever caused it (a command, a timer, the load itself).
    public IReadOnlyList<TimerRequest<PokerInput>> Timers(PokerState state, PresenceView presence, DateTimeOffset now) {
        jira.Track(state);
        NoticeTallies(state);
        return inner.Timers(state, presence, now);
    }

    public IReadOnlyList<PokerEvent> OnWarm(PokerState state, PresenceView presence, DateTimeOffset now) {
        return inner.OnWarm(state, presence, now);
    }

    /// <summary>Pointed = revealed rounds, total = committed rounds (pending previews are not counted).</summary>
    private void NoticeTallies(PokerState state) {
        if (notices is null || state.TeamId is not { } team) {
            return;
        }
        var total = 0;
        var pointed = 0;
        foreach (var round in state.Rounds.Values) {
            if (!round.Pending) {
                total++;
                if (!round.IsVoting) {
                    pointed++;
                }
            }
        }
        if (!_tallies.TryGetValue(state, out var last)) {
            _tallies.Add(state, new StrongBox<(int, int)>((pointed, total)));
            return;
        }
        if (last.Value != (pointed, total)) {
            last.Value = (pointed, total);
            notices.PokerChanged(team, state.SessionId, pointed, total);
        }
    }
}

/// <summary>Unsequenced frames a room hands a subscriber right after its snapshot or replay (cache fills).</summary>
public interface IResumeFill {
    RoomKind Kind { get; }

    IReadOnlyList<ServerFrame> AfterResume(RoomKey room);
}

/// <summary>
/// The poker room's Jira traffic (§f). The <c>JiraMetaLoop</c>: when a room's set of ticket keys grows, the new keys
/// are fetched off-actor in one call (coalesced for 250 ms), every subscriber gets <c>x jira.meta</c>, and the
/// actor gets <see cref="JiraMetaLoaded"/>, which persists only parent keys that changed. Titles stay in memory
/// here. Pending submits are written to Jira off-actor and return as <see cref="JiraPointsResult"/>.
/// </summary>
public sealed class PokerJiraLoop(
    IPokerJiraPort? port,
    TimeProvider time,
    Func<ActorRegistry> registry,
    Func<RoomGateway> gateway,
    ILogger<PokerJiraLoop> logger) : IResumeFill {
    public static readonly TimeSpan Coalesce = TimeSpan.FromMilliseconds(250);
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(10);
    public const string UnavailableCode = "jira_unavailable";

    private readonly ConcurrentDictionary<RoomKey, RoomMeta> _rooms = new();
    private int _pruneAt = 64;

    public RoomKind Kind => RoomKind.Poker;

    /// <summary>Requests metadata for every ticket key of the room it has not requested yet. Actor thread only.</summary>
    public void Track(PokerState state) {
        if (port is null || state.TeamId is not { } team || !state.Jira.Configured) {
            return;
        }
        var room = RoomKey.Poker(state.SessionId);
        var meta = _rooms.GetOrAdd(room, static (_, team) => new RoomMeta(team), team);
        meta.State.SetTarget(state);
        lock (meta) {
            foreach (var id in state.Order) {
                if (state.Rounds[id].TicketKey is { } key && meta.Requested.Add(key)) {
                    meta.Batch.Add(key);
                }
            }
            ScheduleLocked(room, meta);
        }
        if (_rooms.Count > _pruneAt) {
            Prune();
        }
    }

    /// <summary>
    /// The key changed in Jira (a PATCH, transition or points write through the proxy, R15b): every live room of the
    /// team that carries it fetches it again.
    /// </summary>
    public void Invalidate(TeamId team, string key) {
        foreach (var (room, meta) in _rooms) {
            if (meta.Team != team) {
                continue;
            }
            lock (meta) {
                if (meta.Requested.Contains(key)) {
                    meta.Batch.Add(key);
                    ScheduleLocked(room, meta);
                }
            }
        }
    }

    /// <summary>Writes a pending submit to Jira off-actor; the outcome returns to the room as an internal input.</summary>
    public void Submit(RoomKey room, TeamId team, JiraSubmitChanged pending) {
        _ = SubmitAsync(room, team, pending);
    }

    public IReadOnlyList<ServerFrame> AfterResume(RoomKey room) {
        if (!_rooms.TryGetValue(room, out var meta)) {
            return [];
        }
        lock (meta) {
            return [.. meta.Issues.Values.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new XFrame(room, x))];
        }
    }

    private async Task SubmitAsync(RoomKey room, TeamId team, JiraSubmitChanged pending) {
        JiraSubmitResult result;
        try {
            using var timeout = new CancellationTokenSource(CallTimeout, time);
            result = port is null
                ? new JiraSubmitResult(false, "jira_not_configured")
                : await port.SubmitPointsAsync(team, pending.Key, pending.Points, pending.By, timeout.Token);
        } catch (Exception e) {
            PokerRoomLog.JiraCallFailed(logger, e, room, "submit");
            result = new JiraSubmitResult(false, UnavailableCode);
        }
        registry().Post<PokerState, PokerInput>(
            room,
            new JiraPointsResult(pending.RoundId, pending.Key, pending.Points, pending.By, result.Ok, result.Code));
        if (result.Ok) {
            Invalidate(team, pending.Key);
        }
    }

    private void ScheduleLocked(RoomKey room, RoomMeta meta) {
        if (meta.Batch.Count > 0 && !meta.Scheduled) {
            meta.Scheduled = true;
            _ = FetchAsync(room, meta);
        }
    }

    private async Task FetchAsync(RoomKey room, RoomMeta meta) {
        await Task.Delay(Coalesce, time);
        string[] keys;
        lock (meta) {
            keys = [.. meta.Batch.Order(StringComparer.Ordinal)];
            meta.Batch.Clear();
            meta.Scheduled = false;
        }
        IReadOnlyList<JiraMeta> issues;
        try {
            using var timeout = new CancellationTokenSource(CallTimeout, time);
            issues = await port!.LoadMetaAsync(meta.Team, keys, timeout.Token);
        } catch (Exception e) {
            // Metadata is never needed for correctness: the keys stay requested until Jira invalidates one.
            PokerRoomLog.JiraCallFailed(logger, e, room, "meta");
            return;
        }
        if (issues.Count == 0) {
            return;
        }
        lock (meta) {
            foreach (var issue in issues) {
                meta.Issues[issue.Key] = issue;
            }
        }
        var gate = gateway();
        foreach (var issue in issues) {
            gate.Broadcast(room, new XFrame(room, issue));
        }
        registry().Post<PokerState, PokerInput>(room, new JiraMetaLoaded(issues));
    }

    // Rooms whose actor state is gone (evicted or reloaded without Jira) keep nothing once nothing is in flight.
    private void Prune() {
        foreach (var (room, meta) in _rooms) {
            lock (meta) {
                if (!meta.State.TryGetTarget(out _) && !meta.Scheduled) {
                    _rooms.TryRemove(new KeyValuePair<RoomKey, RoomMeta>(room, meta));
                }
            }
        }
        _pruneAt = Math.Max(64, _rooms.Count * 2);
    }

    private sealed class RoomMeta(TeamId team) {
        public TeamId Team { get; } = team;

        public WeakReference<PokerState> State { get; } = new(null!);

        public HashSet<string> Requested { get; } = new(StringComparer.Ordinal);

        public HashSet<string> Batch { get; } = new(StringComparer.Ordinal);

        public Dictionary<string, JiraMeta> Issues { get; } = new(StringComparer.Ordinal);

        public bool Scheduled { get; set; }
    }
}

/// <summary>
/// Hands transport traffic to the actor runtime (U14's <see cref="IRoomGateway"/>, for every room kind). Each
/// subscription is an <see cref="IRoomPeer"/> over the connection; a kick the actor sends goes through
/// <see cref="IClientConnection.Kick"/> so the connection forgets the room too.
/// </summary>
public sealed class RoomGateway : IRoomGateway {
    private readonly ActorRegistry _registry;
    private readonly HashSet<RoomKind> _kinds;
    private readonly Dictionary<RoomKind, IResumeFill[]> _fills;
    private readonly ILogger<RoomGateway> _logger;
    private readonly ConcurrentDictionary<RoomKey, ConcurrentDictionary<ConnectionId, Peer>> _rooms = new();

    public RoomGateway(
        ActorRegistry registry,
        IEnumerable<RoomType> types,
        IEnumerable<IResumeFill> fills,
        ILogger<RoomGateway> logger) {
        _registry = registry;
        _kinds = [.. types.Select(x => x.Kind)];
        _fills = fills.GroupBy(x => x.Kind).ToDictionary(x => x.Key, x => x.ToArray());
        _logger = logger;
    }

    public void Subscribe(IClientConnection connection, RoomKey room, Epoch? epoch, long? seq) {
        if (!_kinds.Contains(room.Kind)) {
            connection.Kick(room, KickCode.NotFound);
            return;
        }
        var peers = _rooms.GetOrAdd(room, static _ => new());
        var peer = peers.GetOrAdd(connection.Viewer.Conn, static (_, x) => new Peer(x.Gateway, x.Connection, x.Room), (Gateway: this, Connection: connection, Room: room));
        _ = SubscribeAsync(peer, epoch, seq);
    }

    public void Unsubscribe(IClientConnection connection, RoomKey room) {
        Forget(room, connection.Viewer.Conn);
        _registry.Unsubscribe(room, connection.Viewer.Conn);
    }

    public void Command(IClientConnection connection, CmdFrame command) {
        if (!_rooms.TryGetValue(command.R, out var peers) || !peers.TryGetValue(connection.Viewer.Conn, out var peer)) {
            connection.Send(new NackFrame(command.R, command.Q, NackCode.NotSubscribed));
            return;
        }
        _ = CommandAsync(peer, command);
    }

    public void Disconnected(IClientConnection connection, IReadOnlyCollection<RoomKey> rooms) {
        foreach (var room in rooms) {
            Forget(room, connection.Viewer.Conn);
            _registry.ConnectionClosed(room, connection.Viewer.Conn);
        }
    }

    /// <summary>Sends an unsequenced frame to every subscriber that already holds the room's snapshot.</summary>
    public void Broadcast(RoomKey room, ServerFrame frame) {
        if (!_rooms.TryGetValue(room, out var peers)) {
            return;
        }
        byte[]? bytes = null;
        foreach (var peer in peers.Values) {
            if (peer.Live) {
                peer.Connection.Send(bytes ??= WireWriter.ToUtf8(frame));
            }
        }
    }

    private async Task SubscribeAsync(Peer peer, Epoch? epoch, long? seq) {
        try {
            var result = await _registry.SubscribeAsync(peer.Room, peer, peer.Connection.Viewer, epoch, seq, CancellationToken.None);
            if (result == PostResult.Accepted) {
                return;
            }
        } catch (Exception e) {
            PokerRoomLog.GatewayFailed(_logger, e, peer.Room, "sub");
        }
        // A room too busy for a new subscription, or one that could not be reached: the client reconnects and resumes.
        Forget(peer.Room, peer.Id);
        peer.Connection.Restart();
    }

    private async Task CommandAsync(Peer peer, CmdFrame command) {
        try {
            var result = await _registry.CommandAsync(peer.Room, peer, peer.Connection.Viewer, command, CancellationToken.None);
            if (result != PostResult.Closed) {
                return;
            }
        } catch (Exception e) {
            PokerRoomLog.GatewayFailed(_logger, e, peer.Room, command.K);
        }
        peer.Connection.Restart();
    }

    private void Forget(RoomKey room, ConnectionId connection) {
        if (_rooms.TryGetValue(room, out var peers)) {
            peers.TryRemove(connection, out _);
            if (peers.IsEmpty) {
                _rooms.TryRemove(new KeyValuePair<RoomKey, ConcurrentDictionary<ConnectionId, Peer>>(room, peers));
            }
        }
    }

    private IReadOnlyList<ServerFrame> FillsAfterResume(RoomKey room) {
        return _fills.TryGetValue(room.Kind, out var fills) ? [.. fills.SelectMany(x => x.AfterResume(room))] : [];
    }

    private sealed class Peer(RoomGateway gateway, IClientConnection connection, RoomKey room) : IRoomPeer {
        private volatile bool _live;

        public ConnectionId Id => Connection.Viewer.Conn;

        public IClientConnection Connection { get; } = connection;

        public RoomKey Room { get; } = room;

        /// <summary>The peer has its snapshot or replay, so unsequenced fills may reach it.</summary>
        public bool Live => _live;

        public bool Send(byte[] frame) {
            var span = frame.AsSpan();
            if (span.StartsWith("{\"t\":\"kick\""u8)) {
                _live = false;
                gateway.Forget(Room, Id);
                Connection.Kick(Room, ((KickFrame)WireParser.ParseServer(frame).Frame!).Code);
                return true;
            }
            var sent = Connection.Send(frame);
            if (sent && (span.StartsWith("{\"t\":\"snap\""u8) || span.StartsWith("{\"t\":\"replay\""u8))) {
                _live = true;
                foreach (var fill in gateway.FillsAfterResume(Room)) {
                    Connection.Send(fill);
                }
            }
            return sent;
        }

        public void Restart() {
            _live = false;
            Connection.Restart();
        }
    }
}

/// <summary>No team roles and no board grants: quick sessions work, team rooms refuse everyone.</summary>
internal sealed class NoTeamAccess : IAccessView {
    public TeamRole? RoleIn(TeamId team, PrincipalId principal) => null;

    public bool HasCurrentGrant(BoardId board, PrincipalId principal, DateTimeOffset? passwordSetAt) => false;
}

internal static partial class PokerRoomLog {
    [LoggerMessage(Level = LogLevel.Warning, Message = "Room {Room}: the Jira {Call} call failed")]
    public static partial void JiraCallFailed(ILogger logger, Exception exception, RoomKey room, string call);

    [LoggerMessage(Level = LogLevel.Error, Message = "Room {Room}: handing {Kind} to the actor failed; the connection restarts")]
    public static partial void GatewayFailed(ILogger logger, Exception exception, RoomKey room, string kind);
}
