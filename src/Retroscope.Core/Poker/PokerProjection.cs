using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;

namespace Retroscope.Core.Poker;

/// <summary>
/// Turns state and events into what one viewer may see (§c.4 vote secrecy). A vote's values go only to its own
/// principal until the round is revealed; everyone else sees its status. Lists are in canonical order: rounds in
/// display order, everything keyed by principal in ordinal order of the id string.
/// </summary>
public static class PokerProjection {
    /// <summary>The public document every viewer shares (R11); the viewer's own values travel in <see cref="Overlay"/>.</summary>
    /// <param name="presence">Present principals, as the actor's presence table describes them.</param>
    public static PokerSnapshot Snapshot(PokerState state, IReadOnlyList<PrincipalInfo> presence) {
        var rounds = state.Order.Select(x => state.Rounds[x]).ToArray();
        return new PokerSnapshot(
            state.SessionId,
            state.Code,
            state.PresenceEnabled,
            state.FollowSpotlight,
            Sorted(state.Observers),
            rounds.Select(PokerApply.Info).ToArray(),
            rounds.SelectMany(PublicVotes).ToArray(),
            Sorted(state.Roster.Values),
            Sorted(presence),
            state.TeamId,
            state.Spotlight is { } spotlight ? new SpotlightInfo(spotlight.Holder, spotlight.RoundId) : null,
            state.CurrentRoundId,
            state.PointDescriptions,
            state.Order
                .Where(state.JiraSubmits.ContainsKey)
                .Select(x => Submit(x, state.JiraSubmits[x]))
                .ToArray());
    }

    /// <summary>The viewer's own votes in rounds still voting, and whether this tab drives the spotlight.</summary>
    public static PokerOverlay Overlay(PokerState state, PrincipalId viewer, ClientId client) {
        var own = state.Order
            .Select(x => state.Rounds[x])
            .Where(x => x.IsVoting && x.Votes.ContainsKey(viewer))
            .Select(x => OwnVote(x.Id, viewer, x.Votes[viewer]))
            .ToArray();
        var held = state.Spotlight is { } spotlight && spotlight.Holder == viewer && spotlight.HolderClient == client;
        return new PokerOverlay(own, held);
    }

    /// <summary>The tx-frame events for <paramref name="viewer"/>; null projects for a viewer with no own votes.</summary>
    public static IReadOnlyList<WireEvent> Events(IReadOnlyList<PokerEvent> events, PrincipalId? viewer) {
        var wire = new List<WireEvent>(events.Count);
        foreach (var evt in events) {
            Project(evt, viewer, wire);
        }
        return wire;
    }

    public static VoteStatus StatusOf(Vote? vote) {
        return vote switch {
            null => VoteStatus.Empty,
            { Locked: true } => VoteStatus.Locked,
            _ => VoteStatus.Selected,
        };
    }

    internal static PrincipalId[] Sorted(IEnumerable<PrincipalId> ids) {
        return ids.OrderBy(x => x.ToString(), StringComparer.Ordinal).ToArray();
    }

    private static PrincipalInfo[] Sorted(IEnumerable<PrincipalInfo> seats) {
        return seats.OrderBy(x => x.Pid.ToString(), StringComparer.Ordinal).ToArray();
    }

    private static void Project(PokerEvent evt, PrincipalId? viewer, List<WireEvent> wire) {
        switch (evt) {
            case VoteChanged e:
                wire.Add(e.Principal == viewer && e.Vote is { } own
                    ? OwnVote(e.RoundId, e.Principal, own)
                    : new PokerVoteState(e.RoundId, e.Principal, Status: StatusOf(e.Vote)));
                break;
            case RoundRevealed e:
                wire.Add(new PokerRoundRevealed(e.RoundId, e.Votes, e.Winner, e.At.ToUnixTimeMilliseconds(), e.By));
                break;
            case RoundReplayed e:
                wire.Add(new PokerRoundReplayed(e.Round.Id));
                wire.Add(new PokerRoundUpdated(e.Round));
                foreach (var entry in e.Votes.Where(x => x.Principal == viewer)) {
                    wire.Add(OwnVote(e.Round.Id, entry.Principal, entry.Vote));
                }
                break;
            case RoundCreated e:
                wire.Add(new PokerRoundCreated(e.Round));
                break;
            case RoundUpdated e:
                wire.Add(new PokerRoundUpdated(e.Round));
                break;
            case RoundDeleted e:
                wire.Add(new PokerRoundDeleted(e.RoundId));
                break;
            case RoundsReordered e:
                wire.Add(new PokerRoundOrder(e.Order));
                break;
            case RoundsReset e:
                wire.Add(new PokerRoundsReset(e.Round));
                break;
            case PointerMoved e:
                wire.Add(new PokerPointer(e.RoundId, e.Cause, e.Nav));
                break;
            case SpotlightChanged e:
                wire.Add(new PokerSpotlight(e.Holder, e.RoundId));
                break;
            case SpotlightMoved e:
                wire.Add(new PokerSpotlight(e.Holder, e.RoundId));
                break;
            case SettingsChanged e:
                wire.Add(new PokerSettingsChanged(e.PresenceEnabled, e.FollowSpotlight));
                break;
            case ObserverSet e:
                wire.Add(new PokerObservers(e.Observers));
                break;
            case RosterSet e:
                wire.Add(new PokerRoster(e.Seats));
                break;
            case PointDescriptionsSet e:
                wire.Add(new TeamSettingsChanged(PointDescriptions: e.PointDescriptions));
                break;
            case JiraSubmitChanged e:
                wire.Add(new PokerJiraPoints(e.RoundId, e.Key, e.Points, e.Status, e.By, e.Code));
                break;
            case JiraPointsRecorded e:
                wire.Add(new PokerJiraPoints(e.Round.Id, e.Key, e.Points, JiraSubmitStatus.Ok, e.By));
                wire.Add(new PokerRoundUpdated(e.Round));
                break;
            case SessionDeleted:
                wire.Add(new PokerSessionDeleted());
                break;
            case SpotlightSettled or AutoRevealArmed or HolderAbsence or JiraSettingsSet:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(evt), evt.GetType().Name, "Unknown poker event.");
        }
    }

    private static IEnumerable<PokerVoteState> PublicVotes(Round round) {
        return round.Votes
            .OrderBy(x => x.Key.ToString(), StringComparer.Ordinal)
            .Select(x => round.IsVoting
                ? new PokerVoteState(round.Id, x.Key, Status: StatusOf(x.Value))
                : OwnVote(round.Id, x.Key, x.Value));
    }

    private static PokerVoteState OwnVote(RoundId round, PrincipalId principal, Vote vote) {
        return new PokerVoteState(round, principal, vote.Points, vote.High, vote.Locked, vote.Abstained);
    }

    private static PokerJiraPoints Submit(RoundId round, JiraSubmit submit) {
        return new PokerJiraPoints(round, submit.Key, submit.Points, submit.Status, submit.By, submit.Code);
    }
}
