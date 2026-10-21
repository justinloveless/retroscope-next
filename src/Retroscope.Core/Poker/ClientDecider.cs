using System.Text.Json;
using Retroscope.Core.Authz;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using static Retroscope.Core.Poker.PokerDecider;

namespace Retroscope.Core.Poker;

/// <summary>Decides one client command for one caller. Permission comes first, then the room's rules.</summary>
internal sealed class ClientDecider(PokerState state, Viewer viewer, CommandContext context, PolicyCheck can) {
    private PrincipalId Me => viewer.Who.Id;

    private ClientId Tab => viewer.Client;

    private DateTimeOffset Now => context.Now;

    public Decision<PokerEvent> VoteSet(PokerVoteSet command) {
        if (VotingRound(command.RoundId, out var round) is { } rejected) {
            return rejected;
        }
        if (!Deck.Contains(command.Points)) {
            return Reject(NackCode.Invalid, "Points must be a deck value.");
        }
        if (command.High is { } high && Deck.Next(command.Points) != high) {
            return Reject(NackCode.Invalid, "A between vote's upper end must be the next deck value.");
        }
        var vote = new Vote(command.Points, command.High, command.Lock == true, false);
        if (round.Votes.TryGetValue(Me, out var current)) {
            if (current == vote) {
                return NoOp();
            }
            if (current.Locked) {
                return Reject(NackCode.VoteLocked, "Your vote is locked.");
            }
        }
        return VoteChange(round, vote);
    }

    public Decision<PokerEvent> LockSet(PokerLockSet command) {
        if (VotingRound(command.RoundId, out var round) is { } rejected) {
            return rejected;
        }
        if (!round.Votes.TryGetValue(Me, out var current) || current.Abstained) {
            return Reject(NackCode.Invalid, "Only a vote with points can be locked.");
        }
        return current.Locked == command.Locked ? NoOp() : VoteChange(round, current with { Locked = command.Locked });
    }

    public Decision<PokerEvent> AbstainSet(PokerAbstainSet command) {
        if (VotingRound(command.RoundId, out var round) is { } rejected) {
            return rejected;
        }
        var abstained = round.Votes.TryGetValue(Me, out var current) && current.Abstained;
        if (abstained == command.Abstain) {
            return NoOp();
        }
        return VoteChange(round, command.Abstain ? Vote.Abstain : null);
    }

    public Decision<PokerEvent> Reveal(PokerRoundReveal command) {
        if (Round(Capability.PokerRevealOrReplay, command.RoundId, out var round) is { } rejected) {
            return rejected;
        }
        if (round.Pending) {
            return Reject(NackCode.RoundPending, "A pending round is a preview and cannot be revealed.");
        }
        if (!round.IsVoting) {
            return NoOp();
        }
        var votes = AutoReveal.FinalVotes(round);
        return Accept(new RoundRevealed(round.Id, votes, AutoReveal.Winner(votes), Now, Me));
    }

    public Decision<PokerEvent> Replay(PokerRoundReplay command) {
        if (Round(Capability.PokerRevealOrReplay, command.RoundId, out var round) is { } rejected) {
            return rejected;
        }
        if (round.IsVoting) {
            return NoOp();
        }
        var info = PokerApply.Info(round) with {
            State = RoundState.Voting,
            Version = round.Version + 1,
            Active = true,
            AutoReveal = false,
            Winner = null,
            RevealedAt = null,
            RevealedBy = null,
        };
        var events = new List<PokerEvent>();
        var votes = new List<VoteEntry>();
        foreach (var (principal, vote) in round.Votes.OrderBy(x => x.Key.ToString(), StringComparer.Ordinal)) {
            if (state.Observers.Contains(principal)) {
                events.Add(new VoteChanged(round.Id, principal, null));
            } else {
                votes.Add(new VoteEntry(principal, vote with { Locked = true }));
            }
        }
        events.Add(new RoundReplayed(info, votes));
        return new Decision<PokerEvent>.Accept(events);
    }

    public Decision<PokerEvent> Create(PokerRoundCreate command) {
        if (Allowed(Capability.PokerManageRounds) is { } rejected) {
            return rejected;
        }
        if (state.Rounds.ContainsKey(command.RoundId)) {
            return Reject(NackCode.Invalid, "That round id is taken.");
        }
        if (!TicketKeys.TryNormalize(command.TicketKey, state.Jira.TicketPrefix, out var ticket)) {
            return Reject(NackCode.Invalid, "Not a Jira issue key.");
        }
        var pending = command.Pending == true;
        if (pending && !HoldsSpotlight()) {
            return Reject(NackCode.NotHolder, "Only the spotlight holder can preview a pending round.");
        }
        var changes = new PokerChanges(state);
        // A preview must never write its key onto the shared committed round.
        if (!pending && command.ForceNew != true && FillPlaceholder(changes, ticket)) {
            return changes.ToDecision();
        }
        AddRound(changes, command.RoundId, ticket, pending);
        if (pending) {
            changes.Add(new PointerMoved(command.RoundId, PointerCause.Create, false));
            changes.Add(new SpotlightChanged(Me, Tab, command.RoundId));
        } else {
            changes.Add(new PointerMoved(command.RoundId, PointerCause.Create, true));
            FollowWithSpotlight(changes, command.RoundId);
        }
        return changes.ToDecision();
    }

    public Decision<PokerEvent> CreateMany(PokerRoundCreateMany command) {
        if (Allowed(Capability.PokerManageRounds) is { } rejected) {
            return rejected;
        }
        if (command.Rounds.Count is 0 or > MaxBatchRounds) {
            return Reject(NackCode.Invalid, $"Add between 1 and {MaxBatchRounds} rounds at a time.");
        }
        if (command.Rounds.DistinctBy(x => x.RoundId).Count() != command.Rounds.Count
            || command.Rounds.Any(x => state.Rounds.ContainsKey(x.RoundId))) {
            return Reject(NackCode.Invalid, "Round ids must be new and distinct.");
        }
        var tickets = new string?[command.Rounds.Count];
        for (var i = 0; i < tickets.Length; i++) {
            if (!TicketKeys.TryNormalize(command.Rounds[i].TicketKey, state.Jira.TicketPrefix, out tickets[i])) {
                return Reject(NackCode.Invalid, "Not a Jira issue key.");
            }
        }
        var changes = new PokerChanges(state);
        var last = state.CurrentRoundId;
        for (var i = 0; i < tickets.Length; i++) {
            if (i == 0 && FillPlaceholder(changes, tickets[0])) {
                continue;
            }
            AddRound(changes, command.Rounds[i].RoundId, tickets[i], false);
            last = command.Rounds[i].RoundId;
        }
        if (last is { } target) {
            changes.Add(new PointerMoved(target, PointerCause.CreateMany, true));
            FollowWithSpotlight(changes, target);
        }
        return changes.ToDecision();
    }

    public Decision<PokerEvent> Next(PokerRoundNext command) {
        if (Allowed(Capability.PokerManageRounds) is { } rejected) {
            return rejected;
        }
        if (state.Rounds.ContainsKey(command.NewRoundId)) {
            return Reject(NackCode.Invalid, "That round id is taken.");
        }
        if (!TicketKeys.TryNormalize(command.TicketKey, state.Jira.TicketPrefix, out var ticket)) {
            return Reject(NackCode.Invalid, "Not a Jira issue key.");
        }
        if (!state.Rounds.TryGetValue(command.FromRoundId, out var from) || !from.Active) {
            return Reject(
                NackCode.Stale,
                "Someone already moved on from that round.",
                NackData.Current(w => WriteRoundId(w, "roundId", state.CurrentRoundId)));
        }
        var changes = new PokerChanges(state);
        AddRound(changes, command.NewRoundId, ticket, false);
        changes.Add(new PointerMoved(command.NewRoundId, PointerCause.Next, true));
        FollowWithSpotlight(changes, command.NewRoundId);
        foreach (var id in state.Order.Where(x => state.Rounds[x].Pending)) {
            changes.RemoveRound(id);
        }
        foreach (var id in changes.State.Order.Where(x => x != command.NewRoundId && changes.State.Rounds[x].Active).ToArray()) {
            changes.Add(new RoundUpdated(PokerApply.Info(changes.State.Rounds[id]) with { Active = false }));
        }
        return changes.ToDecision();
    }

    public Decision<PokerEvent> End(PokerRoundEnd command) {
        if (Round(Capability.PokerManageRounds, command.RoundId, out var round) is { } rejected) {
            return rejected;
        }
        if (round.Pending) {
            return Reject(NackCode.RoundPending, "Commit or discard the pending round instead.");
        }
        return round.Active ? Accept(new RoundUpdated(PokerApply.Info(round) with { Active = false })) : NoOp();
    }

    public Decision<PokerEvent> Activate(PokerRoundActivate command) {
        if (Round(Capability.PokerManageRounds, command.RoundId, out var round) is { } rejected) {
            return rejected;
        }
        if (round.Pending) {
            return Reject(NackCode.RoundPending, "A pending round is a preview.");
        }
        if (!round.IsVoting) {
            return Reject(NackCode.RoundNotVoting, "A revealed round cannot be reactivated; replay it instead.");
        }
        return round.Active ? NoOp() : Accept(new RoundUpdated(PokerApply.Info(round) with { Active = true }));
    }

    public Decision<PokerEvent> SetTicket(PokerRoundSetTicket command) {
        if (Round(Capability.PokerManageRounds, command.RoundId, out var round) is { } rejected) {
            return rejected;
        }
        if (!round.IsVoting) {
            return Reject(NackCode.RoundRevealed, "The round was revealed.");
        }
        if (!round.Active) {
            return Reject(NackCode.RoundInactive, "The round has ended.");
        }
        if (!TicketKeys.TryNormalize(command.TicketKey, state.Jira.TicketPrefix, out var ticket)) {
            return Reject(NackCode.Invalid, "Not a Jira issue key.");
        }
        if (ticket == round.TicketKey) {
            return NoOp();
        }
        if (command.BaseVersion != round.Version) {
            return Reject(NackCode.Stale, "Someone else changed the ticket.", NackData.Current(w => {
                w.WriteString("ticketKey", round.TicketKey);
                w.WriteNumber("version", round.Version);
            }));
        }
        return Accept(new RoundUpdated(WithTicket(round, ticket)));
    }

    public Decision<PokerEvent> CommitPending(PokerRoundCommitPending command) {
        if (Round(Capability.PokerManageRounds, command.RoundId, out var round) is { } rejected) {
            return rejected;
        }
        if (!round.Pending) {
            return NoOp();
        }
        if (round.PendingOwner != Me) {
            return Reject(NackCode.NotHolder, "Only the round's previewer can commit it.");
        }
        return Accept(
            new RoundUpdated(PokerApply.Info(round) with { Pending = false, PendingOwner = null }),
            new PointerMoved(round.Id, PointerCause.Commit, true));
    }

    public Decision<PokerEvent> DiscardPending(PokerRoundDiscardPending command) {
        if (Round(Capability.PokerManageRounds, command.RoundId, out var round) is { } rejected) {
            return rejected;
        }
        if (!round.Pending) {
            return Reject(NackCode.Invalid, "The round was already committed.");
        }
        if (round.PendingOwner != Me) {
            return Reject(NackCode.NotHolder, "Only the round's previewer can discard it.");
        }
        var changes = new PokerChanges(state);
        changes.RemoveRound(round.Id);
        return changes.ToDecision();
    }

    public Decision<PokerEvent> Delete(PokerRoundDelete command) {
        if (Round(Capability.PokerDeleteRound, command.RoundId, out var round) is { } rejected) {
            return rejected;
        }
        if (state.Rounds.Count <= 1) {
            return Reject(NackCode.Invalid, "The last round cannot be deleted.");
        }
        var changes = new PokerChanges(state);
        changes.RemoveRound(round.Id);
        return changes.ToDecision();
    }

    public Decision<PokerEvent> Reorder(PokerRoundReorder command) {
        if (Allowed(Capability.PokerManageRounds) is { } rejected) {
            return rejected;
        }
        var order = command.Order;
        if (order.Count != state.Order.Count || order.Distinct().Count() != order.Count || !order.All(state.Rounds.ContainsKey)) {
            return Reject(NackCode.Stale, "The rounds changed.", NackData.Current(w => {
                w.WriteStartArray("order");
                foreach (var id in state.Order) {
                    w.WriteStringValue(id.Value);
                }
                w.WriteEndArray();
            }));
        }
        return order.SequenceEqual(state.Order) ? NoOp() : Accept(new RoundsReordered([.. order]));
    }

    public Decision<PokerEvent> ClearAll(PokerRoundsClearAll command) {
        if (Allowed(Capability.PokerClearAllRounds) is { } rejected) {
            return rejected;
        }
        if (state.Rounds.ContainsKey(command.NewRoundId)) {
            return Reject(NackCode.Invalid, "That round id is taken.");
        }
        var first = new RoundInfo(command.NewRoundId, 1, 1, RoundState.Voting, true, false, true);
        var changes = new PokerChanges(state);
        changes.Add(new RoundsReset(first));
        changes.Add(new PointerMoved(first.Id, PointerCause.Reset, true));
        if (state.Spotlight is { } spotlight) {
            changes.Add(new SpotlightChanged(spotlight.Holder, spotlight.HolderClient, first.Id));
        }
        return changes.ToDecision();
    }

    public Decision<PokerEvent> UpdateSettings(PokerSettingsUpdate command) {
        if (Allowed(Capability.PokerUpdateSettings) is { } rejected) {
            return rejected;
        }
        if (command.PresenceEnabled is null && command.FollowSpotlight is null) {
            return Reject(NackCode.Invalid, "Nothing to change.");
        }
        if ((command.PresenceEnabled is not null && command.Expected.PresenceEnabled is null)
            || (command.FollowSpotlight is not null && command.Expected.FollowSpotlight is null)) {
            return Reject(NackCode.Invalid, "Every changed setting needs its expected value.");
        }
        if ((command.PresenceEnabled is not null && command.Expected.PresenceEnabled != state.PresenceEnabled)
            || (command.FollowSpotlight is not null && command.Expected.FollowSpotlight != state.FollowSpotlight)) {
            return Reject(NackCode.Stale, "Someone else changed the settings.", NackData.Current(w => {
                w.WriteBoolean("presenceEnabled", state.PresenceEnabled);
                w.WriteBoolean("followSpotlight", state.FollowSpotlight);
            }));
        }
        var presence = command.PresenceEnabled ?? state.PresenceEnabled;
        var follow = command.FollowSpotlight ?? state.FollowSpotlight;
        if (presence == state.PresenceEnabled && follow == state.FollowSpotlight) {
            return NoOp();
        }
        var changes = new PokerChanges(state);
        changes.Add(new SettingsChanged(presence, follow));
        if (presence != state.PresenceEnabled) {
            changes.ArmWatchedRounds(Now);
        }
        return changes.ToDecision();
    }

    public Decision<PokerEvent> SetObserver(PokerObserverSet command) {
        if (Allowed(Capability.PokerSetObserver) is { } rejected) {
            return rejected;
        }
        var target = command.PrincipalId;
        var known = state.Observers.Contains(target)
            || state.Roster.ContainsKey(target)
            || context.Presence.IsPresent(target)
            || state.Rounds.Values.Any(x => x.Votes.ContainsKey(target));
        if (!known) {
            return Reject(NackCode.NotFound, "That principal is not in this session.");
        }
        if (state.Observers.Contains(target) == command.Observer) {
            return NoOp();
        }
        var changes = new PokerChanges(state);
        if (command.Observer) {
            foreach (var id in state.Order) {
                var round = state.Rounds[id];
                if (round.IsVoting && round.Votes.ContainsKey(target)) {
                    changes.Add(new VoteChanged(id, target, null));
                }
            }
        }
        var observers = new HashSet<PrincipalId>(state.Observers);
        if (command.Observer) {
            observers.Add(target);
        } else {
            observers.Remove(target);
        }
        changes.Add(new ObserverSet(target, command.Observer, PokerProjection.Sorted(observers)));
        changes.ArmWatchedRounds(Now);
        return changes.ToDecision();
    }

    public Decision<PokerEvent> TakeSpotlight(PokerSpotlightTake command) {
        if (Round(Capability.PokerSpotlight, command.RoundId, out _) is { } rejected) {
            return rejected;
        }
        if (state.Spotlight is { } spotlight) {
            var heldElsewhere = spotlight.Holder != Me || (spotlight.HolderClient is { } bound && bound != Tab);
            if (heldElsewhere && !command.Force) {
                return Reject(
                    NackCode.SpotlightHeld,
                    "Someone else holds the spotlight.",
                    NackData.Build(w => w.WriteString("holder", spotlight.Holder.Value)));
            }
            if (spotlight.Holder == Me && spotlight.HolderClient == Tab && spotlight.RoundId == command.RoundId) {
                return NoOp();
            }
        }
        var changes = new PokerChanges(state);
        changes.SetSpotlight(Me, Tab, command.RoundId);
        return changes.ToDecision();
    }

    public Decision<PokerEvent> MoveSpotlight(PokerSpotlightMove command) {
        if (Round(Capability.PokerSpotlight, command.RoundId, out _) is { } rejected) {
            return rejected;
        }
        if (state.Spotlight is not { } spotlight
            || spotlight.Holder != Me
            || (spotlight.HolderClient is { } bound && bound != Tab)) {
            return Reject(NackCode.NotHolder, "Only the spotlight holder's tab can move it.");
        }
        if (spotlight.RoundId == command.RoundId && spotlight.HolderClient == Tab) {
            return NoOp();
        }
        return Accept(new SpotlightMoved(Me, Tab, command.RoundId, Now));
    }

    public Decision<PokerEvent> GiveSpotlight(PokerSpotlightGive command) {
        if (Allowed(Capability.PokerSpotlight) is { } rejected) {
            return rejected;
        }
        var spotlight = state.Spotlight;
        if (spotlight is not null && spotlight.Holder != Me) {
            return Reject(NackCode.NotHolder, "Only the spotlight holder can give it away.");
        }
        if (!context.Presence.IsPresent(command.PrincipalId)) {
            return Reject(NackCode.Invalid, "The spotlight can only go to someone who is here.");
        }
        if (spotlight?.Holder == command.PrincipalId) {
            return NoOp();
        }
        if ((spotlight?.RoundId ?? state.CurrentRoundId) is not { } round) {
            return Reject(NackCode.Invalid, "There is no round to put in the spotlight.");
        }
        var changes = new PokerChanges(state);
        changes.SetSpotlight(command.PrincipalId, null, round);
        return changes.ToDecision();
    }

    public Decision<PokerEvent> ReleaseSpotlight() {
        if (Allowed(Capability.PokerSpotlight) is { } rejected) {
            return rejected;
        }
        if (state.Spotlight is not { } spotlight) {
            return NoOp();
        }
        if (spotlight.Holder != Me) {
            return Reject(NackCode.NotHolder, "Only the spotlight holder can release it.");
        }
        var changes = new PokerChanges(state);
        changes.SetSpotlight(null, null, null);
        changes.DiscardPending(Me);
        return changes.ToDecision();
    }

    public Decision<PokerEvent> SubmitJiraPoints(PokerJiraSubmitPoints command) {
        // The policy also denies an unconfigured Jira; asking as if it were configured lets a caller who may
        // submit hear jira_not_configured, and everyone else forbidden.
        var configured = Facts(state, viewer) with { JiraConfigured = true };
        if (!can(viewer.Who, Capability.PokerSubmitJiraPoints, configured, context.Access)) {
            return Reject(NackCode.Forbidden, "Not allowed.");
        }
        if (state.TeamId is null || !state.Jira.Configured) {
            return Reject(NackCode.JiraNotConfigured, "This session's team has no Jira configured.");
        }
        if (Round(Capability.PokerSubmitJiraPoints, command.RoundId, out var round) is { } rejected) {
            return rejected;
        }
        if (round.IsVoting) {
            return Reject(NackCode.Invalid, "Reveal the round before submitting points.");
        }
        if (round.TicketKey is not { } key) {
            return Reject(NackCode.Invalid, "The round has no Jira issue.");
        }
        if (command.Points < 0 || command.Points > MaxJiraPoints || decimal.Remainder(command.Points * 2, 1) != 0) {
            return Reject(NackCode.Invalid, $"Points must be a multiple of 0.5 between 0 and {MaxJiraPoints}.");
        }
        if (state.JiraSubmits.TryGetValue(round.Id, out var submit) && submit.Status == JiraSubmitStatus.Pending) {
            return Reject(NackCode.JiraPending, "A submit for this round is still in flight.");
        }
        return Accept(new JiraSubmitChanged(round.Id, key, command.Points, Me, JiraSubmitStatus.Pending));
    }

    /// <summary>The checks every vote-type command shares, in nack order: observer, permission, round, pending, state, activity.</summary>
    private Decision<PokerEvent>? VotingRound(RoundId id, out Round round) {
        round = null!;
        if (state.Observers.Contains(Me)) {
            return Reject(NackCode.Observer, "Observers do not vote.");
        }
        if (Round(Capability.PokerVote, id, out round) is { } rejected) {
            return rejected;
        }
        if (round.Pending) {
            return Reject(NackCode.RoundPending, "A pending round is a preview and takes no votes.");
        }
        if (!round.IsVoting) {
            return Reject(NackCode.RoundRevealed, "The round was revealed.");
        }
        if (!round.Active) {
            return Reject(NackCode.RoundInactive, "The round has ended.");
        }
        return null;
    }

    private Decision<PokerEvent>? Round(Capability capability, RoundId id, out Round round) {
        round = null!;
        if (Allowed(capability) is { } rejected) {
            return rejected;
        }
        if (!state.Rounds.TryGetValue(id, out var found)) {
            return Reject(NackCode.NotFound, "No such round.");
        }
        round = found;
        return null;
    }

    private Decision<PokerEvent>? Allowed(Capability capability) {
        return can(viewer.Who, capability, Facts(state, viewer), context.Access)
            ? null
            : Reject(NackCode.Forbidden, "Not allowed.");
    }

    private Decision<PokerEvent> VoteChange(Round round, Vote? vote) {
        var events = new List<PokerEvent> { new VoteChanged(round.Id, Me, vote), new AutoRevealArmed(round.Id, Now) };
        if (!state.IsTeamSession && vote is not null && !state.Roster.ContainsKey(Me)) {
            var me = viewer.Who;
            var seats = state.Roster.Values.Append(new PrincipalInfo(me.Id, me.Kind, me.DisplayName, me.AvatarBlobId));
            events.Add(new RosterSet(seats.OrderBy(x => x.Pid.ToString(), StringComparer.Ordinal).ToArray()));
        }
        return new Decision<PokerEvent>.Accept(events);
    }

    /// <summary>The caller's tab may drive the spotlight: the caller holds it and it is bound to this tab or unbound.</summary>
    private bool HoldsSpotlight() {
        return state.Spotlight is { } spotlight
            && spotlight.Holder == Me
            && (spotlight.HolderClient is null || spotlight.HolderClient == Tab);
    }

    /// <summary>The spotlight follows a pointer move only when its holder issued the command.</summary>
    private void FollowWithSpotlight(PokerChanges changes, RoundId target) {
        if (HoldsSpotlight() && changes.State.Spotlight!.RoundId != target) {
            changes.Add(new SpotlightChanged(Me, Tab, target));
        }
    }

    /// <summary>
    /// Parity: a ticket added while the current round has no ticket, is voting and has no votes is written onto
    /// that round instead of creating a new one.
    /// </summary>
    private static bool FillPlaceholder(PokerChanges changes, string? ticket) {
        if (ticket is null
            || changes.State.CurrentRound is not { TicketKey: null, State: RoundState.Voting, Pending: false } current
            || current.Votes.Count > 0) {
            return false;
        }
        changes.Add(new RoundUpdated(WithTicket(current, ticket)));
        return true;
    }

    private static RoundInfo WithTicket(Round round, string? ticket) {
        return PokerApply.Info(round) with { TicketKey = ticket, ParentKey = null, Version = round.Version + 1 };
    }

    private void AddRound(PokerChanges changes, RoundId id, string? ticket, bool pending) {
        var number = changes.State.NextRoundNumber;
        var info = new RoundInfo(
            id, number, 1, RoundState.Voting, true, pending, true, ticket, PendingOwner: pending ? Me : null);
        changes.Add(new RoundCreated(info, changes.State.Order.Count));
    }

    private static void WriteRoundId(Utf8JsonWriter writer, string name, RoundId? id) {
        if (id is { } value) {
            writer.WriteString(name, value.Value);
        } else {
            writer.WriteNull(name);
        }
    }
}
