namespace Retroscope.Core.Authz;

/// <summary>
/// Everything <see cref="Policy.Can(Room.Principal?, Capability, ResourceFacts, Room.IAccessView)"/> decides.
/// Every capability has an allow and a deny row in <c>tests/authz-matrix.csv</c> (U9).
/// </summary>
public enum Capability : byte {
    /// <summary>Subscribe to the room or load it over HTTP (§c.11).</summary>
    Subscribe,

    PokerVote,
    PokerRevealOrReplay,
    PokerManageRounds,
    PokerDeleteRound,
    PokerClearAllRounds,
    PokerUpdateSettings,
    PokerSetObserver,
    PokerSpotlight,
    PokerSubmitJiraPoints,
    PokerDeleteSession,

    RetroAddCard,
    RetroVote,
    RetroComment,
    RetroReady,
    RetroFocus,
    RetroTimer,

    /// <summary>Columns, action-items column, title, config, stages, and deleting action-item cards.</summary>
    RetroFacilitate,
    RetroEndorse,
    RetroViewRoster,
    RetroArchive,
    RetroManageAccess,
    RetroDeleteBoard,

    TeamUpdateActionItem,
    TeamDeleteActionItem,
    TeamManageMembers,
    TeamManageSettings,
    TeamDelete,
    JiraEditIssues,
    JiraConfigure,
}
