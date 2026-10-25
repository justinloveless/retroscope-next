using System.Collections.Frozen;
using System.Text.Json;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using Retroscope.Core.Room;
using Retroscope.Core.Security;

namespace Retroscope.Core.Tests.Retro;

/// <summary>Owns a tag when it is board ‖ principal: enough to test ownership without a key.</summary>
internal sealed class FakeTagger : IAnonTagger {
    public byte[] Tag(BoardId boardId, PrincipalId principalId) =>
        [.. boardId.Value.ToByteArray(), .. principalId.Value.ToByteArray()];

    public bool Owns(ReadOnlySpan<byte> tag, BoardId boardId, PrincipalId principalId) =>
        tag.SequenceEqual(Tag(boardId, principalId));
}

internal sealed class FakeAccess : IAccessView {
    public Dictionary<(TeamId, PrincipalId), TeamRole> Roles { get; } = [];

    public TeamRole? RoleIn(TeamId team, PrincipalId principal) => Roles.TryGetValue((team, principal), out var role) ? role : null;
}

/// <summary>
/// A team board with Good, Bad and Action Items columns, and a cast: Alice owns the team, Bob and Dave are members,
/// Carol is signed in but never a member, and Gus is a legacy guest principal, who is never a caller (UX18).
/// </summary>
internal sealed class RetroFixture {
    public static readonly DateTimeOffset Start = new(2026, 10, 20, 10, 0, 0, TimeSpan.Zero);

    public static readonly TeamId Team = new(Guid.Parse("7e000000-0000-4000-8000-000000000001"));
    public static readonly BoardId Board = new(Guid.Parse("b0000000-0000-4000-8000-000000000001"));
    public static readonly ColumnId Good = new(Guid.Parse("c0000000-0000-4000-8000-000000000001"));
    public static readonly ColumnId Bad = new(Guid.Parse("c0000000-0000-4000-8000-000000000002"));
    public static readonly ColumnId Actions = new(Guid.Parse("c0000000-0000-4000-8000-000000000003"));
    public static readonly EndorsementTypeId Helper = new(Guid.Parse("e7000000-0000-4000-8000-000000000001"));
    public static readonly EndorsementTypeId Retired = new(Guid.Parse("e7000000-0000-4000-8000-000000000002"));

    public static readonly Principal Alice = User("a1000000-0000-4000-8000-000000000001", "Alice");
    public static readonly Principal Bob = User("b2000000-0000-4000-8000-000000000002", "Bob");
    public static readonly Principal Carol = User("c3000000-0000-4000-8000-000000000003", "Carol");
    public static readonly Principal Dave = User("d4000000-0000-4000-8000-000000000005", "Dave");
    public static readonly Principal Gus = new(
        new PrincipalId(Guid.Parse("90000000-0000-4000-8000-000000000004")),
        PrincipalKind.Guest,
        "Sneaky Penguin",
        null,
        false);

    private int _ids;

    public RetroFixture() {
        Decider = new RetroDecider(Tagger);
        Projection = new RetroProjection(Tagger);
        State = new RetroState {
            BoardId = Board,
            Code = "RETRO12345",
            Title = "Sprint 42 retro",
            TeamId = Team,
        };
        State.Columns.Add(new Column { Id = Good, Title = "Good", Color = ColumnColor.Green, SortKey = 0 });
        State.Columns.Add(new Column { Id = Bad, Title = "Bad", Color = ColumnColor.Red, SortKey = 1 });
        State.Columns.Add(new Column { Id = Actions, Title = "Action Items", Color = ColumnColor.Yellow, SortKey = 2, ActionItems = true });
        State.EndorsementTypes.Add(new EndorsementTypeInfo(Helper, "Helper", "🤝", Archived: false));
        State.EndorsementTypes.Add(new EndorsementTypeInfo(Retired, "Retired", "🧓", Archived: true));
        foreach (var (who, role) in new[] { (Alice, TeamRole.Owner), (Bob, TeamRole.Member), (Dave, TeamRole.Member) }) {
            Access.Roles[(Team, who.Id)] = role;
            State.Members.Add(new MemberInfo(who.Id, who.DisplayName, role));
        }
        Present = [Alice, Bob, Dave];
    }

    public FakeTagger Tagger { get; } = new();

    public FakeAccess Access { get; } = new();

    public RetroDecider Decider { get; }

    public RetroProjection Projection { get; }

    public RetroState State { get; }

    public DateTimeOffset Now { get; set; } = Start;

    public List<Principal> Present { get; set; }

    public PresenceView Presence {
        get {
            var principals = Present.Select(p => p.Id).ToFrozenSet();
            var clients = Present.ToFrozenDictionary(p => p.Id, p => new[] { ClientOf(p) }.ToFrozenSet());
            return new PresenceView(principals, clients);
        }
    }

    public static Principal User(string id, string name) => new(new PrincipalId(Guid.Parse(id)), PrincipalKind.User, name, null, false);

    public static ClientId ClientOf(Principal who) => new(who.Id.Value);

    /// <summary>Wire payloads hold lists, which records compare by reference; compare their JSON instead.</summary>
    public static void AssertWire(WireEvent expected, WireEvent actual) {
        Assert.Equal(expected.GetType(), actual.GetType());
        Assert.Equal(Json(expected), Json(actual));
    }

    public static string Json(object payload) => JsonSerializer.Serialize(payload, WireJsonContext.Default.GetTypeInfo(payload.GetType())!);

    public CardId NewCardId() => new(NextGuid(0xCA));

    public CommentId NewCommentId() => new(NextGuid(0xC0));

    public ColumnId NewColumnId() => new(NextGuid(0xC1));

    public EndorsementId NewEndorsementId() => new(NextGuid(0xE0));

    public CommandContext Context(Principal? who) {
        var viewer = who is null ? null : new Viewer(who, ClientOf(who), new ConnectionId(who.Id.Value));
        return new CommandContext(viewer, Now, Presence, Access);
    }

    public Decision<RetroEvent> Decide(Principal? who, RetroInput input) => Decider.Decide(State, input, Context(who));

    /// <summary>Decides, requires an accept and applies it, as the actor would after the commit.</summary>
    public IReadOnlyList<RetroEvent> Do(Principal? who, RetroInput input) {
        var decision = Decide(who, input);
        var accept = Assert.IsType<Decision<RetroEvent>.Accept>(decision);
        foreach (var evt in accept.Events) {
            Decider.Apply(State, evt);
        }
        return accept.Events;
    }

    public void NoOp(Principal? who, RetroInput input) => Assert.IsType<Decision<RetroEvent>.NoOp>(Decide(who, input));

    public Decision<RetroEvent>.Reject Rejects(Principal? who, RetroInput input, NackCode code) {
        var reject = Assert.IsType<Decision<RetroEvent>.Reject>(Decide(who, input));
        Assert.Equal(code, reject.Code);
        return reject;
    }

    public CardId AddCard(Principal who, ColumnId column, string body = "A card", bool anonymous = false) {
        var id = NewCardId();
        Do(who, new RetroCardAdd(id, column, body, anonymous));
        return id;
    }

    public CommentId AddComment(Principal who, CardId card, string body = "A comment", bool anonymous = false) {
        var id = NewCommentId();
        Do(who, new RetroCommentAdd(id, card, body, anonymous));
        return id;
    }

    public void EnableStages(RetroStage stage, bool enforceReadiness = false) {
        State.Config = State.Config with { StagesEnabled = true, EnforceReadiness = enforceReadiness };
        State.Stage = stage;
    }

    private Guid NextGuid(byte prefix) {
        _ids++;
        return Guid.Parse($"{prefix:x2}000000-0000-4000-8000-{_ids:x12}");
    }
}
