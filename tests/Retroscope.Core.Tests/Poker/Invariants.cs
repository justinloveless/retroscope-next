using System.Text;
using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;

namespace Retroscope.Core.Tests.Poker;

internal static class Invariants {
    private static readonly short[] Winners = [0, 1, 2, 3, 5, 8, 13, 21];

    /// <summary>What §b's constraints and the importer's hard invariants demand of a poker session.</summary>
    public static void Hold(PokerState state) {
        Assert.Equal(state.Order.Count, state.Order.Distinct().Count());
        Assert.Equal(state.Rounds.Keys.ToHashSet(), state.Order.ToHashSet());
        var numbers = state.Rounds.Values.Select(x => x.Number).ToArray();
        Assert.Equal(numbers.Length, numbers.Distinct().Count());
        Assert.All(numbers, x => Assert.True(x >= 1 && x < state.NextRoundNumber));

        if (state.Rounds.Count > 0) {
            Assert.NotNull(state.CurrentRoundId);
        }
        if (state.CurrentRoundId is { } current) {
            Assert.Contains(current, state.Rounds.Keys);
        }
        if (state.Spotlight is { } spotlight) {
            Assert.Contains(spotlight.RoundId, state.Rounds.Keys);
        }
        Assert.All(state.JiraSubmits.Keys, x => Assert.Contains(x, state.Rounds.Keys));

        foreach (var round in state.Rounds.Values) {
            Assert.Equal(round.Pending, round.PendingOwner is not null);
            if (round.Pending) {
                Assert.Empty(round.Votes);
                Assert.True(round.IsVoting);
            }
            if (round.IsVoting) {
                Assert.Null(round.Winner);
                Assert.Null(round.RevealedAt);
                Assert.DoesNotContain(round.Votes.Keys, state.Observers.Contains);
            } else {
                Assert.Contains(round.Winner!.Value, Winners);
                Assert.NotNull(round.RevealedAt);
                Assert.All(round.Votes.Values, x => Assert.True(x.Locked));
            }
            Assert.True(round.TicketKey is null || TicketKeys.IsValid(round.TicketKey));
            Assert.True(round.ParentKey is null || TicketKeys.IsValid(round.ParentKey));
            foreach (var vote in round.Votes.Values) {
                Assert.Equal(vote.Abstained, vote.Points is null);
                if (vote.Abstained) {
                    Assert.True(vote.Locked);
                    Assert.Null(vote.High);
                } else {
                    Assert.True(Deck.Contains(vote.Points!.Value));
                }
                if (vote.High is { } high) {
                    Assert.Equal(Deck.Next(vote.Points!.Value), high);
                }
            }
        }
    }
}

internal static class Secrecy {
    /// <summary>No tx event to <paramref name="viewer"/> carries another principal's values while their round votes.</summary>
    public static void FrameHolds(PokerState after, PrincipalId viewer, IReadOnlyList<WireEvent> events) {
        foreach (var evt in events) {
            switch (evt) {
                case PokerVoteState vote when vote.Pid != viewer:
                    Assert.NotNull(vote.Status);
                    Assert.True(
                        vote.Points is null && vote.High is null && vote.Locked is null && vote.Abstained is null,
                        $"{viewer} saw {vote}");
                    break;
                case PokerRoundRevealed revealed:
                    Assert.False(after.Rounds.TryGetValue(revealed.RoundId, out var round) && round.IsVoting);
                    break;
            }
        }
    }

    public static void SnapshotHolds(PokerState state, PrincipalId viewer, ClientId client) {
        var snapshot = PokerProjection.Snapshot(state, []);
        foreach (var vote in snapshot.Votes) {
            if (state.Rounds[vote.RoundId].IsVoting) {
                Assert.NotNull(vote.Status);
                Assert.True(vote.Points is null && vote.High is null && vote.Locked is null && vote.Abstained is null);
            }
        }
        Assert.All(PokerProjection.Overlay(state, viewer, client).Own, x => Assert.Equal(viewer, x.Pid));
    }
}

/// <summary>Everything in a state, memory-only fields included, so a test can prove a decision did not mutate it.</summary>
internal static class Digest {
    public static string Of(PokerState state) {
        var text = new StringBuilder();
        text.Append($"{state.SessionId}|{state.TeamId}|{state.CreatedAt:O}|{state.PresenceEnabled}|{state.FollowSpotlight}|{state.CurrentRoundId}|");
        text.Append($"{state.NextRoundNumber}|{state.WarmUntil:O}|{state.Deleted}|{state.Jira}|{state.PointDescriptions.Count}|");
        text.AppendJoin(',', state.Observers.Select(x => x.ToString()).Order(StringComparer.Ordinal)).Append('|');
        text.AppendJoin(',', state.Order).Append('|');
        text.AppendJoin(',', state.Roster.Values.OrderBy(x => x.Pid.ToString())).Append('|');
        text.AppendJoin(',', state.JiraSubmits.OrderBy(x => x.Key.ToString())).Append('|');
        if (state.Spotlight is { } s) {
            text.Append($"{s.Holder}/{s.HolderClient}/{s.RoundId}/{s.Dirty}/{s.MovedAt:O}/{s.HolderAbsentSince:O}|");
        }
        foreach (var round in state.Rounds.Values.OrderBy(x => x.Id.ToString())) {
            text.Append($"{round.Id}:{round.Number}:{round.Version}:{round.TicketKey}:{round.ParentKey}:{round.State}:");
            text.Append($"{round.Active}:{round.Pending}:{round.AutoReveal}:{round.PendingOwner}:{round.Winner}:");
            text.Append($"{round.RevealedAt:O}:{round.RevealedBy}:{round.Jira}:{round.AutoRevealGen}:{round.ArmedAt:O}:");
            text.AppendJoin(',', round.Votes.OrderBy(x => x.Key.ToString()).Select(x => $"{x.Key}={x.Value}")).Append('|');
        }
        return text.ToString();
    }
}
