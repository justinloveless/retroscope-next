using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Core.Poker;

/// <summary>Server-only auto-reveal (R14). Runs for every active round, not just the one someone is viewing.</summary>
public static class AutoReveal {
    /// <summary>Active, voting, not pending and auto-reveal enabled: the rounds auto-reveal watches.</summary>
    public static bool IsWatched(Round round) {
        return round is { Active: true, State: RoundState.Voting, Pending: false, AutoReveal: true };
    }

    /// <summary>
    /// <c>eligible = (present − observers) ∪ {absent principals whose vote is locked or abstained}</c>, where
    /// present is the grace-smoothed presence set, or roster ∪ present when presence is disabled. An absent
    /// principal with an unlocked vote does not block.
    /// </summary>
    public static HashSet<PrincipalId> Eligible(PokerState state, Round round, PresenceView presence) {
        var eligible = new HashSet<PrincipalId>(presence.Principals);
        if (!state.PresenceEnabled) {
            eligible.UnionWith(state.Roster.Keys);
        }
        foreach (var (principal, vote) in round.Votes) {
            if (vote.Locked) {
                eligible.Add(principal);
            }
        }
        eligible.ExceptWith(state.Observers);
        return eligible;
    }

    /// <summary>Every eligible principal is locked or abstained, and there is at least one.</summary>
    public static bool IsReady(PokerState state, Round round, PresenceView presence) {
        if (!IsWatched(round)) {
            return false;
        }
        var eligible = Eligible(state, round, presence);
        return eligible.Count > 0 && eligible.All(x => round.Votes.TryGetValue(x, out var vote) && vote.Locked);
    }

    /// <summary>The votes after a reveal: every unlocked vote becomes an abstain. Seats with no vote stay absent.</summary>
    public static IReadOnlyList<RevealedVote> FinalVotes(Round round) {
        return round.Votes
            .OrderBy(x => x.Key.ToString(), StringComparer.Ordinal)
            .Select(x => x.Value.Locked
                ? new RevealedVote(x.Key, x.Value.Abstained, x.Value.Points, x.Value.High)
                : new RevealedVote(x.Key, true))
            .ToArray();
    }

    public static short Winner(IReadOnlyList<RevealedVote> votes) {
        return Scoring.Winner(votes.Select(x => new Vote(x.Points, x.High, true, x.Abstained)));
    }
}
