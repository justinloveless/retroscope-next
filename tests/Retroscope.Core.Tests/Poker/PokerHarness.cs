using System.Collections.Frozen;
using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Core.Tests.Poker;

/// <summary>
/// Plays the actor: decides against the state, applies accepted events, tracks presence and runs the presence,
/// warm-up and timer hooks the way the runtime will.
/// </summary>
internal sealed class PokerHarness {
    public static readonly DateTimeOffset Start = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private readonly Dictionary<PrincipalId, HashSet<ClientId>> _connected = [];
    private readonly HashSet<PrincipalId> _present = [];
    private readonly Dictionary<PrincipalId, Principal> _principals = [];

    private PokerHarness(PokerState state, TestAccess access) {
        State = state;
        Access = access;
    }

    public PokerState State { get; }

    public TestAccess Access { get; }

    public PokerDomain Domain { get; } = new(TestPolicy.Can);

    public DateTimeOffset Now { get; set; } = Start;

    /// <summary>Every accepted decision's events, in order (one entry per transaction).</summary>
    public List<IReadOnlyList<PokerEvent>> Log { get; } = [];

    /// <summary>Called after every commit, as the actor fans out.</summary>
    public Action<IReadOnlyList<PokerEvent>>? Applied { get; set; }

    public PresenceView Presence {
        get {
            return new PresenceView(
                _present.ToFrozenSet(),
                _connected.ToFrozenDictionary(x => x.Key, x => x.Value.ToFrozenSet()));
        }
    }

    public static SessionId Session { get; } = new(Id(0xA0));

    public static TeamId TeamId { get; } = new(Id(0xB0));

    public static RoundId R(int n) => new(Id(0x1000 + n));

    public static Guid Id(int n) => new($"00000000-0000-4000-8000-{n:x12}");

    public static Viewer Viewer(int n, string name, PrincipalKind kind = PrincipalKind.User, int tab = 0) {
        var principal = new Principal(new PrincipalId(Id(n)), kind, name, null, false);
        return new Viewer(principal, new ClientId(Id(0x500 + (n * 10) + tab)), new ConnectionId(Id(0x900 + (n * 10) + tab)));
    }

    public static Viewer Alice { get; } = Viewer(1, "Alice");

    public static Viewer Bob { get; } = Viewer(2, "Bob");

    public static Viewer Carol { get; } = Viewer(3, "Carol");

    public static Viewer Dave { get; } = Viewer(4, "Dave");

    /// <summary>A legacy guest principal: never a caller (UX18).</summary>
    public static Viewer Guest { get; } = Viewer(5, "Gus", PrincipalKind.Guest);

    /// <summary>Signed in, not a member of the team.</summary>
    public static Viewer Outsider { get; } = Viewer(6, "Olga");

    /// <summary>Alice is the owner, Dave an admin, Bob and Carol members; round 1 is current and the room is warm.</summary>
    public static PokerHarness Team(bool jira = true) {
        var access = new TestAccess();
        access.Roles[Alice.Who.Id] = TeamRole.Owner;
        access.Roles[Dave.Who.Id] = TeamRole.Admin;
        access.Roles[Bob.Who.Id] = TeamRole.Member;
        access.Roles[Carol.Who.Id] = TeamRole.Member;
        var state = NewState();
        state.Jira = jira ? new TeamJiraSettings(true, "ABC", true) : TeamJiraSettings.None;
        foreach (var member in new[] { Alice, Bob, Carol, Dave }) {
            state.Roster[member.Who.Id] = Info(member);
        }
        return new PokerHarness(state, access);
    }

    public static PrincipalInfo Info(Viewer viewer) {
        return new PrincipalInfo(viewer.Who.Id, viewer.Who.Kind, viewer.Who.DisplayName);
    }

    public CommandContext Context(Viewer? viewer) => new(viewer, Now, Presence, Access);

    public Decision<PokerEvent> Decide(Viewer? viewer, PokerInput input) {
        return Domain.Decide(State, input, Context(viewer));
    }

    /// <summary>Decides and, when accepted, applies the events as the actor does after commit.</summary>
    public Decision<PokerEvent> Send(Viewer? viewer, PokerInput input) {
        var decision = Decide(viewer, input);
        if (decision is Decision<PokerEvent>.Accept accept) {
            Commit(accept.Events);
        }
        return decision;
    }

    public IReadOnlyList<PokerEvent> Accepted(Viewer? viewer, PokerInput input) {
        var decision = Send(viewer, input);
        return Assert.IsType<Decision<PokerEvent>.Accept>(decision).Events;
    }

    public void Commit(IReadOnlyList<PokerEvent> events) {
        if (events.Count == 0) {
            return;
        }
        foreach (var evt in events) {
            Domain.Apply(State, evt);
        }
        Log.Add(events);
        Applied?.Invoke(events);
    }

    public void Join(Viewer viewer) {
        _principals[viewer.Who.Id] = viewer.Who;
        var tabs = _connected.TryGetValue(viewer.Who.Id, out var set) ? set : _connected[viewer.Who.Id] = [];
        tabs.Add(viewer.Client);
        if (_present.Add(viewer.Who.Id)) {
            Commit(Domain.OnPresence(State, new PresenceDelta(PresenceChange.Joined, viewer.Who.Id), Presence, Now));
        } else {
            Commit(Domain.OnPresence(
                State, new PresenceDelta(PresenceChange.ClientConnected, viewer.Who.Id, viewer.Client), Presence, Now));
        }
    }

    /// <summary>The tab disconnected and its grace ran out; the principal leaves when it was their last tab.</summary>
    public void Disconnect(Viewer viewer) {
        var tabs = _connected[viewer.Who.Id];
        tabs.Remove(viewer.Client);
        Commit(Domain.OnPresence(
            State, new PresenceDelta(PresenceChange.ClientGone, viewer.Who.Id, viewer.Client), Presence, Now));
        if (tabs.Count == 0) {
            _connected.Remove(viewer.Who.Id);
            _present.Remove(viewer.Who.Id);
            Commit(Domain.OnPresence(State, new PresenceDelta(PresenceChange.Left, viewer.Who.Id), Presence, Now));
        }
    }

    public void Warm() => Commit(Domain.OnWarm(State, Presence, Now));

    public IReadOnlyList<TimerRequest<PokerInput>> Timers() => Domain.Timers(State, Presence, Now);

    /// <summary>Advances the clock and fires every timer due by then, earliest first, re-reading timers after each.</summary>
    public void Advance(TimeSpan by) {
        var until = Now + by;
        while (Timers().Where(x => x.DueAt <= until).MinBy(x => x.DueAt) is { } due) {
            Now = due.DueAt > Now ? due.DueAt : Now;
            var decision = Send(null, due.Command);
            if (decision is not Decision<PokerEvent>.Accept) {
                break;
            }
        }
        Now = until;
    }

    public IReadOnlyList<PrincipalInfo> PresenceInfo() {
        return _present.Select(x => new PrincipalInfo(x, _principals[x].Kind, _principals[x].DisplayName)).ToArray();
    }

    public Round Round(int n) => State.Rounds[R(n)];

    private static PokerState NewState() {
        var state = new PokerState {
            SessionId = Session,
            Code = "TEAM01",
            TeamId = TeamId,
            WarmUntil = Start,
            CurrentRoundId = R(1),
            NextRoundNumber = 2,
        };
        state.Rounds.Add(R(1), new Round { Id = R(1), Number = 1 });
        state.Order.Add(R(1));
        return state;
    }
}

internal sealed class TestAccess : IAccessView {
    public Dictionary<PrincipalId, TeamRole> Roles { get; } = [];

    public TeamRole? RoleIn(TeamId team, PrincipalId principal) {
        return team == PokerHarness.TeamId && Roles.TryGetValue(principal, out var role) ? role : null;
    }
}
