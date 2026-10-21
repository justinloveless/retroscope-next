using System.Text.Json;
using Retroscope.Core.Authz;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Core.Poker;

/// <summary>The permission check deciders run first; <see cref="Policy.Can(Principal?, Capability, ResourceFacts, IAccessView)"/> in production.</summary>
public delegate bool PolicyCheck(Principal who, Capability capability, ResourceFacts facts, IAccessView access);

/// <summary>Pure: reads the state, never mutates it (§c.4 poker rules).</summary>
public static class PokerDecider {
    public const int MaxBatchRounds = 50;
    public const decimal MaxJiraPoints = 1000m;

    public static Decision<PokerEvent> Decide(PokerState state, PokerInput input, CommandContext context, PolicyCheck can) {
        // Internal transitions after a delete are leftovers (timers, Jira replies, fan-out); a client still hears
        // not_found, so it learns why nothing happened.
        if (state.Deleted && input is not ClientCommand) {
            return NoOp();
        }
        var decision = input switch {
            ClientCommand c => DecideClient(state, c.Command, context, can),
            AutoRevealDue c => DecideAutoReveal(state, c, context),
            SpotlightGraceDue c => DecideSpotlightGrace(state, c, context),
            SpotlightSettleDue => state.Spotlight is { Dirty: true } spotlight
                ? Accept(new SpotlightSettled(spotlight.RoundId, spotlight.HolderClient))
                : NoOp(),
            JiraPointsResult c => DecideJiraResult(state, c, context),
            JiraMetaLoaded c => DecideJiraMeta(state, c),
            RosterChanged c => DecideRoster(state, c, context),
            PointDescriptionsChanged c => Accept(new PointDescriptionsSet(c.PointDescriptions)),
            JiraSettingsChanged c => c.Settings == state.Jira ? NoOp() : Accept(new JiraSettingsSet(c.Settings)),
            DeleteSession => DecideDeleteSession(state, context, can),
            _ => throw new ArgumentOutOfRangeException(nameof(input), input.GetType().Name, "Unknown poker input."),
        };
        return WithSettledSpotlight(state, decision);
    }

    /// <summary>A dirty spotlight position rides along in the next committed transaction (§c.8).</summary>
    private static Decision<PokerEvent> WithSettledSpotlight(PokerState state, Decision<PokerEvent> decision) {
        if (decision is not Decision<PokerEvent>.Accept accept
            || state.Spotlight is not { Dirty: true } spotlight
            || !accept.Events.Any(x => x.Durability == EventDurability.Committed)
            || accept.Events.Any(x => x is SpotlightChanged or SpotlightSettled or SessionDeleted or RoundsReset)) {
            return decision;
        }
        return new Decision<PokerEvent>.Accept([.. accept.Events, new SpotlightSettled(spotlight.RoundId, spotlight.HolderClient)]);
    }

    private static Decision<PokerEvent> DecideClient(
        PokerState state,
        PokerCommand command,
        CommandContext context,
        PolicyCheck can) {
        if (context.Viewer is not { } viewer) {
            return Reject(NackCode.Forbidden, "A client command needs a caller.");
        }
        if (state.Deleted) {
            return Reject(NackCode.NotFound, "The session was deleted.");
        }
        var caller = new ClientDecider(state, viewer, context, can);
        return command switch {
            PokerVoteSet c => caller.VoteSet(c),
            PokerLockSet c => caller.LockSet(c),
            PokerAbstainSet c => caller.AbstainSet(c),
            PokerRoundReveal c => caller.Reveal(c),
            PokerRoundReplay c => caller.Replay(c),
            PokerRoundCreate c => caller.Create(c),
            PokerRoundCreateMany c => caller.CreateMany(c),
            PokerRoundNext c => caller.Next(c),
            PokerRoundEnd c => caller.End(c),
            PokerRoundActivate c => caller.Activate(c),
            PokerRoundSetTicket c => caller.SetTicket(c),
            PokerRoundCommitPending c => caller.CommitPending(c),
            PokerRoundDiscardPending c => caller.DiscardPending(c),
            PokerRoundDelete c => caller.Delete(c),
            PokerRoundReorder c => caller.Reorder(c),
            PokerRoundsClearAll c => caller.ClearAll(c),
            PokerSettingsUpdate c => caller.UpdateSettings(c),
            PokerObserverSet c => caller.SetObserver(c),
            PokerSpotlightTake c => caller.TakeSpotlight(c),
            PokerSpotlightMove c => caller.MoveSpotlight(c),
            PokerSpotlightGive c => caller.GiveSpotlight(c),
            PokerSpotlightRelease => caller.ReleaseSpotlight(),
            PokerJiraSubmitPoints c => caller.SubmitJiraPoints(c),
            _ => Reject(NackCode.Invalid, "Not a poker command."),
        };
    }

    private static Decision<PokerEvent> DecideAutoReveal(PokerState state, AutoRevealDue due, CommandContext context) {
        if (!state.Rounds.TryGetValue(due.RoundId, out var round)
            || round.AutoRevealGen != due.Gen
            || context.Now < state.WarmUntil
            || context.Now - round.ArmedAt < PokerTiming.AutoRevealDelay
            || !AutoReveal.IsReady(state, round, context.Presence)) {
            return NoOp();
        }
        var votes = AutoReveal.FinalVotes(round);
        return Accept(new RoundRevealed(round.Id, votes, AutoReveal.Winner(votes), context.Now, null));
    }

    private static Decision<PokerEvent> DecideSpotlightGrace(PokerState state, SpotlightGraceDue due, CommandContext context) {
        if (state.Spotlight is not { HolderAbsentSince: { } since } spotlight
            || spotlight.Holder != due.Holder
            || context.Presence.IsPresent(due.Holder)
            || context.Now - since < PokerTiming.SpotlightGrace) {
            return NoOp();
        }
        var changes = new PokerChanges(state);
        changes.SetSpotlight(null, null, null);
        changes.DiscardPending(due.Holder);
        return changes.ToDecision();
    }

    private static Decision<PokerEvent> DecideJiraResult(PokerState state, JiraPointsResult result, CommandContext context) {
        if (!state.JiraSubmits.TryGetValue(result.RoundId, out var submit)
            || submit.Status != JiraSubmitStatus.Pending
            || submit.Key != result.Key
            || submit.Points != result.Points
            || !state.Rounds.TryGetValue(result.RoundId, out var round)) {
            return NoOp();
        }
        if (!result.Ok) {
            return Accept(new JiraSubmitChanged(
                round.Id, result.Key, result.Points, result.By, JiraSubmitStatus.Failed, result.Code ?? "jira_error"));
        }
        if (round.TicketKey != result.Key) {
            return Accept(new JiraSubmitChanged(round.Id, result.Key, result.Points, result.By, JiraSubmitStatus.Ok));
        }
        var info = PokerApply.Info(round) with {
            Jira = new JiraPointsInfo(result.Points, context.Now.ToUnixTimeMilliseconds(), result.By),
        };
        return Accept(new JiraPointsRecorded(info, result.Key, result.Points, result.By));
    }

    private static Decision<PokerEvent> DecideJiraMeta(PokerState state, JiraMetaLoaded loaded) {
        var parents = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var issue in loaded.Issues) {
            parents[issue.Key] = TicketKeys.IsValid(issue.ParentKey) ? issue.ParentKey : null;
        }
        var events = new List<PokerEvent>();
        foreach (var id in state.Order) {
            var round = state.Rounds[id];
            if (round.TicketKey is { } key && parents.TryGetValue(key, out var parent) && parent != round.ParentKey) {
                events.Add(new RoundUpdated(PokerApply.Info(round) with { ParentKey = parent }));
            }
        }
        return events.Count == 0 ? NoOp() : Accept([.. events]);
    }

    private static Decision<PokerEvent> DecideRoster(PokerState state, RosterChanged changed, CommandContext context) {
        var seats = changed.Seats.DistinctBy(x => x.Pid).OrderBy(x => x.Pid.ToString(), StringComparer.Ordinal).ToArray();
        if (seats.Length == state.Roster.Count && seats.All(x => state.Roster.TryGetValue(x.Pid, out var seat) && seat == x)) {
            return NoOp();
        }
        var changes = new PokerChanges(state);
        changes.Add(new RosterSet(seats));
        changes.ArmWatchedRounds(context.Now);
        return changes.ToDecision();
    }

    private static Decision<PokerEvent> DecideDeleteSession(PokerState state, CommandContext context, PolicyCheck can) {
        if (context.Viewer is not { } viewer || !can(viewer.Who, Capability.PokerDeleteSession, Facts(state, viewer), context.Access)) {
            return Reject(NackCode.Forbidden, "Only a team admin or the quick session's creator may delete it.");
        }
        return Accept(new SessionDeleted());
    }

    internal static ResourceFacts Facts(PokerState state, Viewer viewer) {
        return new ResourceFacts(
            RoomKey.Poker(state.SessionId),
            Team: state.TeamId,
            CreatedBy: state.CreatedBy,
            IsObserver: state.Observers.Contains(viewer.Who.Id),
            JiraConfigured: state.Jira.Configured,
            MembersCanEditJira: state.Jira.MembersCanEdit);
    }

    internal static Decision<PokerEvent> Accept(params PokerEvent[] events) => new Decision<PokerEvent>.Accept(events);

    internal static Decision<PokerEvent> NoOp() => new Decision<PokerEvent>.NoOp();

    internal static Decision<PokerEvent> Reject(NackCode code, string detail, JsonElement? data = null) {
        return new Decision<PokerEvent>.Reject(code, detail, data);
    }
}
