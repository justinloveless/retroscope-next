using System.Collections.Frozen;
using System.Text.Json.Serialization.Metadata;
using Retroscope.Core.Ids;

namespace Retroscope.Core.Protocol;

public enum KindDirection : byte {
    Command,
    Event,
    Unsequenced,
}

[Flags]
public enum RoomScope : byte {
    Poker = 1,
    Board = 2,
    Team = 4,
}

/// <param name="Ephemeral">The client drops an unsent entry of this kind after 10 s offline instead of resending it.</param>
public sealed record CatalogEntry(
    string Kind,
    KindDirection Direction,
    RoomScope Scope,
    JsonTypeInfo TypeInfo,
    string Summary,
    bool Ephemeral = false) {
    public Type Type => TypeInfo.Type;

    public bool AllowedIn(RoomKind room) => (Scope & Catalog.ScopeOf(room)) != 0;
}

/// <summary>
/// The single list of every command, event and unsequenced kind. It drives the envelope parser, the frame
/// writer, <c>web/src/net/protocol.gen.js</c> and <c>docs/protocol.md</c>, so client and server kinds
/// cannot drift.
/// </summary>
public static class Catalog {
    public const int ProtocolVersion = 1;

    private static readonly WireJsonContext Json = WireJsonContext.Default;

    public static IReadOnlyList<CatalogEntry> Entries { get; } = [
        Cmd("poker.vote.set", RoomScope.Poker, Json.PokerVoteSet,
            "Select a card (and `high` for a between vote); `lock:true` selects and locks at once."),
        Cmd("poker.lock.set", RoomScope.Poker, Json.PokerLockSet, "Lock or unlock the caller's vote."),
        Cmd("poker.abstain.set", RoomScope.Poker, Json.PokerAbstainSet,
            "Abstain (locked) or un-abstain (empties the slot)."),
        Cmd("poker.round.reveal", RoomScope.Poker, Json.PokerRoundReveal,
            "Reveal; unlocked votes become abstains."),
        Cmd("poker.round.replay", RoomScope.Poker, Json.PokerRoundReplay,
            "Back to voting with non-abstain votes kept and locked."),
        Cmd("poker.round.create", RoomScope.Poker, Json.PokerRoundCreate,
            "New round, or fill the placeholder; `pending` previews for the spotlight holder."),
        Cmd("poker.round.createMany", RoomScope.Poker, Json.PokerRoundCreateMany,
            "Batch add (at most 50) from a Jira bucket."),
        Cmd("poker.round.next", RoomScope.Poker, Json.PokerRoundNext,
            "Next round from `fromRoundId` (must still be active, else `stale`)."),
        Cmd("poker.round.end", RoomScope.Poker, Json.PokerRoundEnd, "Deactivate a round."),
        Cmd("poker.round.activate", RoomScope.Poker, Json.PokerRoundActivate, "Reactivate a voting round."),
        Cmd("poker.round.setTicket", RoomScope.Poker, Json.PokerRoundSetTicket,
            "Set or clear the ticket key; `baseVersion` precondition."),
        Cmd("poker.round.commitPending", RoomScope.Poker, Json.PokerRoundCommitPending,
            "Turn the caller's pending preview into a real round."),
        Cmd("poker.round.discardPending", RoomScope.Poker, Json.PokerRoundDiscardPending,
            "Delete the caller's pending preview."),
        Cmd("poker.round.delete", RoomScope.Poker, Json.PokerRoundDelete,
            "Delete a round (spotlight and pointer repaired in the same transaction)."),
        Cmd("poker.round.reorder", RoomScope.Poker, Json.PokerRoundReorder,
            "New order; must be a permutation of the current rounds, else `stale`."),
        Cmd("poker.rounds.clearAll", RoomScope.Poker, Json.PokerRoundsClearAll,
            "Delete every round and start again at round 1."),
        Cmd("poker.settings.update", RoomScope.Poker, Json.PokerSettingsUpdate,
            "Change session settings; `expected` precondition per field."),
        Cmd("poker.observer.set", RoomScope.Poker, Json.PokerObserverSet, "Make a principal an observer or a voter."),
        Cmd("poker.spotlight.take", RoomScope.Poker, Json.PokerSpotlightTake,
            "Take the spotlight; without `force` a held spotlight nacks `spotlight_held`."),
        Cmd("poker.spotlight.move", RoomScope.Poker, Json.PokerSpotlightMove,
            "Move the spotlight (holder's bound tab only; ephemeral-durable).", ephemeral: true),
        Cmd("poker.spotlight.give", RoomScope.Poker, Json.PokerSpotlightGive, "Hand the spotlight to a present principal."),
        Cmd("poker.spotlight.release", RoomScope.Poker, Json.PokerSpotlightRelease, "Release the spotlight."),
        Cmd("poker.jira.submitPoints", RoomScope.Poker, Json.PokerJiraSubmitPoints,
            "Write story points to the round's Jira issue."),

        Cmd("retro.card.add", RoomScope.Board, Json.RetroCardAdd, "Add a card."),
        Cmd("retro.card.edit", RoomScope.Board, Json.RetroCardEdit, "Edit a card; `baseVersion` precondition."),
        Cmd("retro.card.delete", RoomScope.Board, Json.RetroCardDelete, "Delete a card."),
        Cmd("retro.vote.set", RoomScope.Board, Json.RetroVoteSet, "Vote on (or un-vote) a card."),
        Cmd("retro.comment.add", RoomScope.Board, Json.RetroCommentAdd, "Comment on a card."),
        Cmd("retro.comment.delete", RoomScope.Board, Json.RetroCommentDelete, "Delete a comment."),
        Cmd("retro.column.add", RoomScope.Board, Json.RetroColumnAdd, "Add a column (facilitator)."),
        Cmd("retro.column.update", RoomScope.Board, Json.RetroColumnUpdate,
            "Rename or recolour a column; `baseVersion` precondition."),
        Cmd("retro.column.delete", RoomScope.Board, Json.RetroColumnDelete, "Delete a column and its cards."),
        Cmd("retro.column.reorder", RoomScope.Board, Json.RetroColumnReorder,
            "New column order; must be a permutation, else `stale`."),
        Cmd("retro.column.setActionItems", RoomScope.Board, Json.RetroColumnSetActionItems,
            "Choose the action-items column (or none)."),
        Cmd("retro.board.rename", RoomScope.Board, Json.RetroBoardRename, "Rename the board; `expected` precondition."),
        Cmd("retro.config.update", RoomScope.Board, Json.RetroConfigUpdate,
            "Patch board settings; `expected` precondition per field."),
        Cmd("retro.stage.set", RoomScope.Board, Json.RetroStageSet, "Change stage (compare-and-set on `from`)."),
        Cmd("retro.ready.set", RoomScope.Board, Json.RetroReadySet, "Mark the caller ready for the current stage."),
        Cmd("retro.focus.set", RoomScope.Board, Json.RetroFocusSet,
            "Focus a card (compare-and-set on `from`).", ephemeral: true),
        Cmd("retro.timer.start", RoomScope.Board, Json.RetroTimerStart,
            "Start the timer (compare-and-set on `from`).", ephemeral: true),
        Cmd("retro.timer.pause", RoomScope.Board, Json.RetroTimerPause, "Pause the timer.", ephemeral: true),
        Cmd("retro.timer.resume", RoomScope.Board, Json.RetroTimerResume, "Resume the timer.", ephemeral: true),
        Cmd("retro.timer.reset", RoomScope.Board, Json.RetroTimerReset, "Reset the timer to idle.", ephemeral: true),
        Cmd("retro.timer.setFlags", RoomScope.Board, Json.RetroTimerSetFlags,
            "Toggle the alarm and music flags.", ephemeral: true),
        Cmd("retro.endorse.give", RoomScope.Board, Json.RetroEndorseGive, "Endorse a team member (team boards)."),
        Cmd("retro.endorse.revoke", RoomScope.Board, Json.RetroEndorseRevoke, "Withdraw the caller's endorsement."),

        Cmd("team.action.assign", RoomScope.Team, Json.TeamActionAssign, "Assign an action item (or unassign)."),
        Cmd("team.action.done", RoomScope.Team, Json.TeamActionDone, "Mark an action item done or open."),
        Cmd("team.action.delete", RoomScope.Team, Json.TeamActionDelete, "Delete an action item (admin)."),

        Ev("poker.vote.state", RoomScope.Poker, Json.PokerVoteState,
            "A vote changed: values to the voter, status only to everyone else."),
        Ev("poker.round.revealed", RoomScope.Poker, Json.PokerRoundRevealed, "Every vote with values, and the winner."),
        Ev("poker.round.replayed", RoomScope.Poker, Json.PokerRoundReplayed, "The round is voting again."),
        Ev("poker.round.created", RoomScope.Poker, Json.PokerRoundCreated, "A new round."),
        Ev("poker.round.updated", RoomScope.Poker, Json.PokerRoundUpdated, "A round's new state (whole round)."),
        Ev("poker.round.deleted", RoomScope.Poker, Json.PokerRoundDeleted, "A round was deleted."),
        Ev("poker.round.order", RoomScope.Poker, Json.PokerRoundOrder, "The new round order."),
        Ev("poker.rounds.reset", RoomScope.Poker, Json.PokerRoundsReset, "Every round was replaced by this round 1."),
        Ev("poker.pointer", RoomScope.Poker, Json.PokerPointer, "The current-round pointer moved (`nav` rule, R13)."),
        Ev("poker.spotlight", RoomScope.Poker, Json.PokerSpotlight, "Spotlight holder and round; empty = released."),
        Ev("poker.settings", RoomScope.Poker, Json.PokerSettingsChanged, "Session settings."),
        Ev("poker.observers", RoomScope.Poker, Json.PokerObservers, "Every observer."),
        Ev("poker.roster", RoomScope.Poker, Json.PokerRoster, "Team members shown as seats."),
        Ev("poker.jira.points", RoomScope.Poker, Json.PokerJiraPoints, "Jira story-point submit state, shared by all."),
        Ev("poker.session.deleted", RoomScope.Poker, Json.PokerSessionDeleted, "The session is gone; a kick follows."),

        Ev("retro.card.added", RoomScope.Board, Json.RetroCardAdded, "A new card."),
        Ev("retro.card.edited", RoomScope.Board, Json.RetroCardEdited, "A card's new body and version."),
        Ev("retro.card.deleted", RoomScope.Board, Json.RetroCardDeleted, "A card was deleted."),
        Ev("retro.vote", RoomScope.Board, Json.RetroVoteChanged, "A card's vote count (voters when shown)."),
        Ev("retro.comment.added", RoomScope.Board, Json.RetroCommentAdded, "A new comment."),
        Ev("retro.comment.deleted", RoomScope.Board, Json.RetroCommentDeleted, "A comment was deleted."),
        Ev("retro.column.added", RoomScope.Board, Json.RetroColumnAdded, "A new column."),
        Ev("retro.column.updated", RoomScope.Board, Json.RetroColumnUpdated, "A column's new state."),
        Ev("retro.column.deleted", RoomScope.Board, Json.RetroColumnDeleted, "A column and its cards were deleted."),
        Ev("retro.column.order", RoomScope.Board, Json.RetroColumnOrder, "The new column order."),
        Ev("retro.column.actionItems", RoomScope.Board, Json.RetroActionItemsColumn,
            "The action-items column; empty = none."),
        Ev("retro.board.meta", RoomScope.Board, Json.RetroBoardMeta, "Title and archived flag."),
        Ev("retro.config", RoomScope.Board, Json.RetroConfigChanged, "The whole board configuration."),
        Ev("retro.stage", RoomScope.Board, Json.RetroStageChanged, "The current stage."),
        Ev("retro.readiness", RoomScope.Board, Json.RetroReadiness, "Who is ready for the stage."),
        Ev("retro.focus", RoomScope.Board, Json.RetroFocus, "The focused card; empty = none."),
        Ev("retro.timer", RoomScope.Board, Json.RetroTimer, "Timer state on the server clock."),
        Ev("retro.endorsement.added", RoomScope.Board, Json.RetroEndorsementAdded,
            "An endorsement (team members only)."),
        Ev("retro.endorsement.removed", RoomScope.Board, Json.RetroEndorsementRemoved, "An endorsement was withdrawn."),
        Ev("retro.members", RoomScope.Board, Json.RetroMembers, "The team's members with roles (team members only)."),

        Ev("team.action.added", RoomScope.Team, Json.TeamActionAdded, "A new action item."),
        Ev("team.action.updated", RoomScope.Team, Json.TeamActionUpdated, "An action item's new state."),
        Ev("team.action.text", RoomScope.Team, Json.TeamActionText, "Text synced from the source card."),
        Ev("team.action.deleted", RoomScope.Team, Json.TeamActionDeleted, "An action item was deleted."),
        Ev("team.action.comments", RoomScope.Team, Json.TeamActionComments,
            "Comment count changed; refetch the comments over HTTP."),
        Ev("team.members", RoomScope.Team, Json.TeamMembers, "Every member with role."),
        Ev("team.boards.changed", RoomScope.Team, Json.TeamBoardsChanged, "Refetch the board list."),
        Ev("team.poker.changed", RoomScope.Team, Json.TeamPokerChanged, "A session's progress changed."),
        Ev("team.settings", RoomScope.Poker | RoomScope.Board | RoomScope.Team, Json.TeamSettingsChanged,
            "Team settings that this room uses."),

        Ev("presence.join", RoomScope.Poker | RoomScope.Board, Json.PresenceJoin, "A principal arrived."),
        Ev("presence.leave", RoomScope.Poker | RoomScope.Board, Json.PresenceLeave,
            "A principal left (after the grace period)."),
        Ev("presence.update", RoomScope.Poker | RoomScope.Board, Json.PresenceUpdate,
            "A present principal's name or avatar changed."),

        new("jira.meta", KindDirection.Unsequenced, RoomScope.Poker, Json.JiraMeta,
            "Jira issue title, type, status and parent (in-memory cache fill)."),
    ];

    private static readonly FrozenDictionary<string, CatalogEntry> Commands = ByKind(KindDirection.Command);
    private static readonly FrozenDictionary<string, CatalogEntry> Events = ByKind(KindDirection.Event);
    private static readonly FrozenDictionary<string, CatalogEntry> UnsequencedKinds = ByKind(KindDirection.Unsequenced);
    private static readonly FrozenDictionary<Type, CatalogEntry> ByType = Entries.ToFrozenDictionary(x => x.Type);

    public static bool TryGetCommand(string kind, out CatalogEntry entry) => TryGet(Commands, kind, out entry);

    public static bool TryGetEvent(string kind, out CatalogEntry entry) => TryGet(Events, kind, out entry);

    public static bool TryGetUnsequenced(string kind, out CatalogEntry entry) => TryGet(UnsequencedKinds, kind, out entry);

    public static CatalogEntry EntryOf(object payload) {
        return ByType.TryGetValue(payload.GetType(), out var entry)
            ? entry
            : throw new ArgumentException($"{payload.GetType().Name} is not a wire payload in the catalog.", nameof(payload));
    }

    public static string KindOf(object payload) => EntryOf(payload).Kind;

    public static RoomScope ScopeOf(RoomKind room) => room switch {
        RoomKind.Poker => RoomScope.Poker,
        RoomKind.Board => RoomScope.Board,
        RoomKind.Team => RoomScope.Team,
        _ => throw new ArgumentOutOfRangeException(nameof(room)),
    };

    public static JsonTypeInfo SnapshotTypeInfo(RoomKind room) => room switch {
        RoomKind.Poker => Json.PokerSnapshot,
        RoomKind.Board => Json.RetroSnapshot,
        RoomKind.Team => Json.TeamSnapshot,
        _ => throw new ArgumentOutOfRangeException(nameof(room)),
    };

    /// <summary>Null for rooms that have no per-viewer overlay.</summary>
    public static JsonTypeInfo? OverlayTypeInfo(RoomKind room) => room switch {
        RoomKind.Poker => Json.PokerOverlay,
        RoomKind.Board => Json.RetroOverlay,
        RoomKind.Team => null,
        _ => throw new ArgumentOutOfRangeException(nameof(room)),
    };

    private static CatalogEntry Cmd(string kind, RoomScope scope, JsonTypeInfo typeInfo, string summary, bool ephemeral = false) {
        return new CatalogEntry(kind, KindDirection.Command, scope, typeInfo, summary, ephemeral);
    }

    private static CatalogEntry Ev(string kind, RoomScope scope, JsonTypeInfo typeInfo, string summary) {
        return new CatalogEntry(kind, KindDirection.Event, scope, typeInfo, summary);
    }

    private static FrozenDictionary<string, CatalogEntry> ByKind(KindDirection direction) {
        return Entries.Where(x => x.Direction == direction).ToFrozenDictionary(x => x.Kind, StringComparer.Ordinal);
    }

    private static bool TryGet(FrozenDictionary<string, CatalogEntry> map, string kind, out CatalogEntry entry) {
        return map.TryGetValue(kind, out entry!);
    }
}
