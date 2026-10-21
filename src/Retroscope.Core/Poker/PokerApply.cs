using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;

namespace Retroscope.Core.Poker;

public static class PokerApply {
    public static void Apply(PokerState state, PokerEvent evt) {
        switch (evt) {
            case VoteChanged e:
                var votes = state.Rounds[e.RoundId].Votes;
                if (e.Vote is { } vote) {
                    votes[e.Principal] = vote;
                } else {
                    votes.Remove(e.Principal);
                }
                break;
            case RoundRevealed e:
                ApplyReveal(state.Rounds[e.RoundId], e);
                break;
            case RoundReplayed e:
                var replayed = state.Rounds[e.Round.Id];
                CopyInfo(replayed, e.Round);
                replayed.Votes.Clear();
                foreach (var entry in e.Votes) {
                    replayed.Votes.Add(entry.Principal, entry.Vote);
                }
                break;
            case RoundCreated e:
                var created = new Round { Id = e.Round.Id, Number = e.Round.Number };
                CopyInfo(created, e.Round);
                state.Rounds.Add(created.Id, created);
                state.Order.Insert(e.SortKey, created.Id);
                state.NextRoundNumber = Math.Max(state.NextRoundNumber, created.Number + 1);
                break;
            case RoundUpdated e:
                CopyInfo(state.Rounds[e.Round.Id], e.Round);
                break;
            case RoundDeleted e:
                RemoveRound(state, e.RoundId);
                break;
            case RoundsReordered e:
                state.Order.Clear();
                state.Order.AddRange(e.Order);
                break;
            case RoundsReset e:
                state.Rounds.Clear();
                state.Order.Clear();
                state.JiraSubmits.Clear();
                state.CurrentRoundId = null;
                var first = new Round { Id = e.Round.Id, Number = e.Round.Number };
                CopyInfo(first, e.Round);
                state.Rounds.Add(first.Id, first);
                state.Order.Add(first.Id);
                state.NextRoundNumber = first.Number + 1;
                break;
            case PointerMoved e:
                state.CurrentRoundId = e.RoundId;
                break;
            case SpotlightChanged e:
                state.Spotlight = e.Holder is { } holder && e.RoundId is { } round
                    ? new Spotlight {
                        Holder = holder,
                        HolderClient = e.HolderClient,
                        RoundId = round,
                        HolderAbsentSince = state.Spotlight?.Holder == holder ? state.Spotlight.HolderAbsentSince : null,
                    }
                    : null;
                break;
            case SpotlightMoved e:
                var moved = state.Spotlight!;
                moved.RoundId = e.RoundId;
                moved.HolderClient = e.HolderClient;
                moved.Dirty = true;
                moved.MovedAt = e.At;
                break;
            case SpotlightSettled:
                state.Spotlight!.Dirty = false;
                break;
            case SettingsChanged e:
                state.PresenceEnabled = e.PresenceEnabled;
                state.FollowSpotlight = e.FollowSpotlight;
                break;
            case ObserverSet e:
                if (e.Observer) {
                    state.Observers.Add(e.Principal);
                } else {
                    state.Observers.Remove(e.Principal);
                }
                break;
            case RosterSet e:
                state.Roster.Clear();
                foreach (var seat in e.Seats) {
                    state.Roster[seat.Pid] = seat;
                }
                break;
            case PointDescriptionsSet e:
                state.PointDescriptions = e.PointDescriptions;
                break;
            case JiraSubmitChanged e:
                if (e.Status == JiraSubmitStatus.Ok) {
                    state.JiraSubmits.Remove(e.RoundId);
                } else {
                    state.JiraSubmits[e.RoundId] = new JiraSubmit(e.Key, e.Points, e.By, e.Status, e.Code);
                }
                break;
            case JiraPointsRecorded e:
                CopyInfo(state.Rounds[e.Round.Id], e.Round);
                state.JiraSubmits.Remove(e.Round.Id);
                break;
            case SessionDeleted:
                state.Deleted = true;
                break;
            case AutoRevealArmed e:
                var armed = state.Rounds[e.RoundId];
                armed.AutoRevealGen++;
                armed.ArmedAt = e.At;
                break;
            case HolderAbsence e:
                state.Spotlight!.HolderAbsentSince = e.Since;
                break;
            case JiraSettingsSet e:
                state.Jira = e.Settings;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(evt), evt.GetType().Name, "Unknown poker event.");
        }
    }

    private static void ApplyReveal(Round round, RoundRevealed e) {
        round.State = RoundState.Revealed;
        round.Winner = e.Winner;
        round.RevealedAt = e.At;
        round.RevealedBy = e.By;
        round.AutoRevealGen++;
        round.Votes.Clear();
        foreach (var vote in e.Votes) {
            round.Votes.Add(vote.Pid, new Vote(vote.Points, vote.High, true, vote.Abstained));
        }
    }

    private static void RemoveRound(PokerState state, RoundId id) {
        state.Rounds.Remove(id);
        state.Order.Remove(id);
        state.JiraSubmits.Remove(id);
        if (state.CurrentRoundId == id) {
            state.CurrentRoundId = null;
        }
    }

    private static void CopyInfo(Round round, RoundInfo info) {
        round.Number = info.Number;
        round.Version = info.Version;
        round.State = info.State;
        round.Active = info.Active;
        round.Pending = info.Pending;
        round.AutoReveal = info.AutoReveal;
        round.TicketKey = info.TicketKey;
        round.ParentKey = info.ParentKey;
        round.PendingOwner = info.PendingOwner;
        round.Winner = info.Winner;
        round.RevealedAt = info.RevealedAt is { } at ? DateTimeOffset.FromUnixTimeMilliseconds(at) : null;
        round.RevealedBy = info.RevealedBy;
        round.Jira = info.Jira is { } jira
            ? new JiraPoints(jira.Points, DateTimeOffset.FromUnixTimeMilliseconds(jira.At), jira.By)
            : null;
    }

    /// <summary>The wire shape of a round, as carried by round events and snapshots.</summary>
    public static RoundInfo Info(Round round) {
        return new RoundInfo(
            round.Id,
            round.Number,
            round.Version,
            round.State,
            round.Active,
            round.Pending,
            round.AutoReveal,
            round.TicketKey,
            round.ParentKey,
            round.PendingOwner,
            round.Winner,
            round.RevealedAt?.ToUnixTimeMilliseconds(),
            round.RevealedBy,
            round.Jira is { } jira ? new JiraPointsInfo(jira.Points, jira.At.ToUnixTimeMilliseconds(), jira.By) : null);
    }
}
