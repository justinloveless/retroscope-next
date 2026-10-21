using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;

namespace Retroscope.Core.Poker;

/// <summary>
/// One poker session as its actor holds it. Mutated only by <see cref="PokerApply"/>, after the events committed.
/// </summary>
public sealed class PokerState {
    public SessionId SessionId { get; set; }

    public string Code { get; set; } = "";

    /// <summary>Null for a quick session.</summary>
    public TeamId? TeamId { get; set; }

    public PrincipalId? CreatedBy { get; set; }

    public bool PresenceEnabled { get; set; } = true;

    public bool FollowSpotlight { get; set; } = true;

    public HashSet<PrincipalId> Observers { get; } = [];

    public Spotlight? Spotlight { get; set; }

    /// <summary>Rounds in display order; a round's index is its <c>sort_key</c>.</summary>
    public List<RoundId> Order { get; } = [];

    public Dictionary<RoundId, Round> Rounds { get; } = [];

    /// <summary>The "latest" pointer.</summary>
    public RoundId? CurrentRoundId { get; set; }

    public int NextRoundNumber { get; set; } = 1;

    /// <summary>Team sessions: the team's members. Quick sessions: principals who voted (R27).</summary>
    public Dictionary<PrincipalId, PrincipalInfo> Roster { get; } = [];

    /// <summary>Load time plus <see cref="PokerTiming.WarmUp"/>: no auto-reveal while clients reconnect.</summary>
    public DateTimeOffset WarmUntil { get; set; }

    public IReadOnlyDictionary<string, string> PointDescriptions { get; set; } = new Dictionary<string, string>();

    public TeamJiraSettings Jira { get; set; } = TeamJiraSettings.None;

    /// <summary>Jira submits that are in flight or failed; memory only. A successful one lands in <see cref="Round.Jira"/>.</summary>
    public Dictionary<RoundId, JiraSubmit> JiraSubmits { get; } = [];

    public bool Deleted { get; set; }

    public bool IsTeamSession => TeamId is not null;

    public Round? CurrentRound => CurrentRoundId is { } id ? Rounds.GetValueOrDefault(id) : null;

    public PokerState Clone() {
        var clone = new PokerState {
            SessionId = SessionId,
            Code = Code,
            TeamId = TeamId,
            CreatedBy = CreatedBy,
            PresenceEnabled = PresenceEnabled,
            FollowSpotlight = FollowSpotlight,
            Spotlight = Spotlight?.Clone(),
            CurrentRoundId = CurrentRoundId,
            NextRoundNumber = NextRoundNumber,
            WarmUntil = WarmUntil,
            PointDescriptions = PointDescriptions,
            Jira = Jira,
            Deleted = Deleted,
        };
        clone.Observers.UnionWith(Observers);
        clone.Order.AddRange(Order);
        foreach (var (id, round) in Rounds) {
            clone.Rounds.Add(id, round.Clone());
        }
        foreach (var (id, seat) in Roster) {
            clone.Roster.Add(id, seat);
        }
        foreach (var (id, submit) in JiraSubmits) {
            clone.JiraSubmits.Add(id, submit);
        }
        return clone;
    }
}

public sealed class Round {
    public RoundId Id { get; set; }

    /// <summary>The stable "Round N" label, allocated by the server.</summary>
    public int Number { get; set; }

    /// <summary>The precondition for <c>poker.round.setTicket</c> (R5).</summary>
    public int Version { get; set; } = 1;

    /// <summary>Null for a placeholder round.</summary>
    public string? TicketKey { get; set; }

    public string? ParentKey { get; set; }

    public RoundState State { get; set; }

    public bool Active { get; set; } = true;

    public bool Pending { get; set; }

    public bool AutoReveal { get; set; } = true;

    public PrincipalId? PendingOwner { get; set; }

    public Dictionary<PrincipalId, Vote> Votes { get; } = [];

    public short? Winner { get; set; }

    public DateTimeOffset? RevealedAt { get; set; }

    /// <summary>Null with <see cref="RevealedAt"/> set means an auto-reveal.</summary>
    public PrincipalId? RevealedBy { get; set; }

    public JiraPoints? Jira { get; set; }

    /// <summary>Bumped whenever auto-reveal must be re-evaluated; a due timer of an older generation is ignored.</summary>
    public int AutoRevealGen { get; set; }

    /// <summary>The last vote, observer or presence change that re-armed auto-reveal.</summary>
    public DateTimeOffset ArmedAt { get; set; }

    public bool IsVoting => State == RoundState.Voting;

    public Round Clone() {
        var clone = new Round {
            Id = Id,
            Number = Number,
            Version = Version,
            TicketKey = TicketKey,
            ParentKey = ParentKey,
            State = State,
            Active = Active,
            Pending = Pending,
            AutoReveal = AutoReveal,
            PendingOwner = PendingOwner,
            Winner = Winner,
            RevealedAt = RevealedAt,
            RevealedBy = RevealedBy,
            Jira = Jira,
            AutoRevealGen = AutoRevealGen,
            ArmedAt = ArmedAt,
        };
        foreach (var (principal, vote) in Votes) {
            clone.Votes.Add(principal, vote);
        }
        return clone;
    }
}

/// <param name="Points">A deck value; null when abstained.</param>
/// <param name="High">A "between" vote's upper end: the next deck value after <paramref name="Points"/>.</param>
public readonly record struct Vote(short? Points, short? High, bool Locked, bool Abstained) {
    public static readonly Vote Abstain = new(null, null, true, true);
}

public sealed class Spotlight {
    public PrincipalId Holder { get; set; }

    /// <summary>The tab that may move the spotlight; null lets any tab of the holder bind on its next move.</summary>
    public ClientId? HolderClient { get; set; }

    public RoundId RoundId { get; set; }

    /// <summary>A move was applied but not persisted yet (ephemeral-durable, §c.8).</summary>
    public bool Dirty { get; set; }

    public DateTimeOffset MovedAt { get; set; }

    /// <summary>When the holder principal left the room; the spotlight is released 3 minutes later.</summary>
    public DateTimeOffset? HolderAbsentSince { get; set; }

    public Spotlight Clone() => (Spotlight)MemberwiseClone();
}

/// <summary>The last points written to Jira for a round (decimal, F15).</summary>
public sealed record JiraPoints(decimal Points, DateTimeOffset At, PrincipalId By);

public sealed record JiraSubmit(string Key, decimal Points, PrincipalId By, JiraSubmitStatus Status, string? Code = null);

/// <param name="TicketPrefix">Prepended to a bare issue number typed as a ticket key.</param>
public sealed record TeamJiraSettings(bool Configured, string? TicketPrefix, bool MembersCanEdit) {
    public static readonly TeamJiraSettings None = new(false, null, true);
}
