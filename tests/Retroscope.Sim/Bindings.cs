using System.Collections.Frozen;
using Retroscope.Core.Authz;
using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Core.Team;
using Retroscope.Server.Realtime.Actors;

namespace Retroscope.Sim;

/// <summary>Bugs the harness must catch when they are planted (DESIGN §j: the harness has teeth).</summary>
internal enum InjectedBug : byte {
    None,

    /// <summary>The persisted cursor is read one lower, as a <c>q &lt; cursor</c> check would behave.</summary>
    Cursor,

    /// <summary>The poker decider no longer sees a stale <c>baseVersion</c> or <c>expected</c> value.</summary>
    Precondition,
}

/// <summary>The team membership cache (U23's <c>IAccessView</c>): replaced as a whole after every member commit.</summary>
internal sealed class SimAccess(TeamId team) : IAccessView {
    private volatile FrozenDictionary<PrincipalId, TeamRole> _roles = FrozenDictionary<PrincipalId, TeamRole>.Empty;

    public void Set(IEnumerable<MemberInfo> members) => _roles = members.ToFrozenDictionary(x => x.Pid, x => x.Role);

    public TeamRole? RoleIn(TeamId teamId, PrincipalId principal) {
        return teamId == team && _roles.TryGetValue(principal, out var role) ? role : null;
    }

    public bool HasCurrentGrant(BoardId board, PrincipalId principal, DateTimeOffset? passwordSetAt) => false;
}

/// <summary>
/// The real domain, watched: every decision on a client command is reported to the <see cref="Oracle"/>, and an
/// injected bug can rewrite the command first.
/// </summary>
internal sealed class WatchedDomain<TState, TInput, TEvent>(
    IRoomDomain<TState, TInput, TEvent> inner,
    Oracle oracle,
    Func<TInput, Command?> commandOf,
    Func<TEvent, EventDurability> durabilityOf,
    Func<bool> live,
    Func<TState, TInput, TInput>? tamper = null) : IRoomDomain<TState, TInput, TEvent> {
    public Decision<TEvent> Decide(TState s, TInput c, CommandContext ctx) {
        var decision = inner.Decide(s, tamper is null ? c : tamper(s, c), ctx);
        // A dead process's actor may still drain its queue; what it decides never reaches anyone.
        if (live() && commandOf(c) is { } command) {
            oracle.Decided(command, decision switch {
                Decision<TEvent>.Accept accept when accept.Events.Any(x => durabilityOf(x) == EventDurability.Committed) => DecisionSeen.Committed,
                Decision<TEvent>.Accept => DecisionSeen.AppliedInMemory,
                Decision<TEvent>.NoOp => DecisionSeen.NoOp,
                _ => DecisionSeen.Rejected,
            });
        }
        return decision;
    }

    public void Apply(TState s, TEvent e) => inner.Apply(s, e);

    public IReadOnlyList<TEvent> OnPresence(TState s, PresenceDelta d, PresenceView p, DateTimeOffset now) => inner.OnPresence(s, d, p, now);

    public IReadOnlyList<TimerRequest<TInput>> Timers(TState s, PresenceView p, DateTimeOffset now) => inner.Timers(s, p, now);

    public IReadOnlyList<TEvent> OnWarm(TState s, PresenceView p, DateTimeOffset now) => inner.OnWarm(s, p, now);
}

/// <summary>The real poker binding (U21) over the real <see cref="PokerDomain"/>, watched by the oracle.</summary>
internal static class SimPokerBinding {
    public static Server.Realtime.Rooms.PokerBinding Create(Oracle oracle, InjectedBug bug, Func<bool> live) {
        return new Server.Realtime.Rooms.PokerBinding(new WatchedDomain<PokerState, PokerInput, PokerEvent>(
            new PokerDomain(),
            oracle,
            x => (x as ClientCommand)?.Command,
            x => x.Durability,
            live,
            bug == InjectedBug.Precondition ? DropPreconditions : null));
    }

    /// <summary>The planted bug: overwrite commands always carry the values the server holds now.</summary>
    private static PokerInput DropPreconditions(PokerState state, PokerInput input) {
        return input switch {
            ClientCommand { Command: PokerRoundSetTicket set } when state.Rounds.TryGetValue(set.RoundId, out var round) =>
                set with { BaseVersion = round.Version },
            ClientCommand { Command: PokerSettingsUpdate update } =>
                update with { Expected = new PokerSettingsValues(state.PresenceEnabled, state.FollowSpotlight) },
            _ => input,
        };
    }
}

internal sealed class TeamBinding(Oracle oracle, TimeProvider time, Func<bool> live) : IRoomBinding<TeamState, TeamInput, TeamEvent> {
    public RoomKind Kind => RoomKind.Team;

    public IRoomDomain<TeamState, TeamInput, TeamEvent> Domain { get; } = new WatchedDomain<TeamState, TeamInput, TeamEvent>(
        TeamDecider.Instance,
        oracle,
        x => (x as TeamInput.Client)?.Command,
        _ => EventDurability.Committed,
        live);

    public bool TracksPresence => false;

    public TeamInput? FromClient(Command command) => command is TeamCommand team ? TeamInput.From(team) : null;

    public bool CanSubscribe(TeamState state, Viewer viewer, IAccessView access) {
        return Policy.Can(viewer, Capability.Subscribe, TeamDecider.Facts(state), access);
    }

    public RoomSnapshot Snapshot(TeamState state, IReadOnlyList<PrincipalInfo> presence, bool forMembers) {
        return TeamProjection.Snapshot(state, time.GetUtcNow());
    }

    public RoomOverlay? Overlay(TeamState state, Viewer viewer) => null;

    public IReadOnlyList<WireEvent> Project(TeamState state, IReadOnlyList<TeamEvent> events, PrincipalId viewer, bool viewerIsMember) {
        return [.. events.Select(TeamProjection.ToWire).OfType<WireEvent>()];
    }

    public bool IsDeleted(TeamState state) => state.Deleted;
}
