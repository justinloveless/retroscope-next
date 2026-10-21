using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Core.Team;
using Retroscope.Core.Tests.Authz;

namespace Retroscope.Core.Tests.Team;

/// <summary>A team room with an owner, an admin, a member and one open action item.</summary>
internal sealed class TeamFixture {
    public static readonly DateTimeOffset Now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);

    public TeamFixture() {
        State = new TeamState(TeamId, "Platform");
        foreach (var (principal, role) in new[] { (Owner, TeamRole.Owner), (Admin, TeamRole.Admin), (Member, TeamRole.Member) }) {
            Access.SetRole(TeamId, principal.Id, role);
            State.Members[principal.Id] = new MemberInfo(principal.Id, principal.DisplayName, role);
        }
        Item = NewItem(new CardId(Guid.CreateVersion7()));
        State.ActionItems[Item.Id] = Item;
    }

    public TeamId TeamId { get; } = new(Guid.CreateVersion7());

    public Principal Owner { get; } = User("Olga");

    public Principal Admin { get; } = User("Ada");

    public Principal Member { get; } = User("Mo");

    public Principal Outsider { get; } = User("Otto");

    public FakeAccess Access { get; } = new();

    public TeamState State { get; }

    public ActionItemInfo Item { get; }

    public static Principal User(string name) => new(new PrincipalId(Guid.CreateVersion7()), PrincipalKind.User, name, null, false);

    public static ActionItemInfo NewItem(CardId sourceCard) {
        return new ActionItemInfo(
            new ActionItemId(Guid.CreateVersion7()),
            "Fix the flaky test",
            1,
            Now.AddDays(-1).ToUnixTimeMilliseconds(),
            0,
            new BoardId(Guid.CreateVersion7()),
            sourceCard);
    }

    public Decision<TeamEvent> Decide(TeamInput input, Principal? by = null) {
        var viewer = by is null ? null : new Viewer(by, new ClientId(Guid.CreateVersion7()), new ConnectionId(Guid.CreateVersion7()));
        return TeamDecider.Instance.Decide(State, input, new CommandContext(viewer, Now, PresenceView.Empty, Access));
    }

    public Decision<TeamEvent> Command(TeamCommand command, Principal by) => Decide(TeamInput.From(command), by);

    /// <summary>Decides and, when accepted, applies, as the actor does after a successful commit.</summary>
    public Decision<TeamEvent> Run(TeamInput input, Principal? by = null) {
        var decision = Decide(input, by);
        if (decision is Decision<TeamEvent>.Accept accept) {
            foreach (var evt in accept.Events) {
                TeamDecider.Instance.Apply(State, evt);
            }
        }
        return decision;
    }

    public static TEvent Single<TEvent>(Decision<TeamEvent> decision) where TEvent : TeamEvent {
        var accept = Assert.IsType<Decision<TeamEvent>.Accept>(decision);
        return Assert.IsType<TEvent>(Assert.Single(accept.Events));
    }

    public static void Rejected(Decision<TeamEvent> decision, NackCode code) {
        Assert.Equal(code, Assert.IsType<Decision<TeamEvent>.Reject>(decision).Code);
    }
}
