using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Core.Text;

namespace Retroscope.Core.Team;

/// <summary>
/// The team room's wire view. Only members subscribe and they all see the same thing, so there is no per-viewer
/// overlay.
/// </summary>
public static class TeamProjection {
    /// <summary>Closed action items stay in the snapshot this long after they were done.</summary>
    public static readonly TimeSpan ClosedItemWindow = TimeSpan.FromDays(30);

    public static TeamSnapshot Snapshot(TeamState state, DateTimeOffset now) {
        var closedSince = (now - ClosedItemWindow).ToUnixTimeMilliseconds();
        var items = state.ActionItems.Values
            .Where(x => x.DoneAt is null || x.DoneAt >= closedSince)
            .OrderByDescending(x => x.CreatedAt)
            .ThenBy(x => x.Id.Value)
            .ToList();
        return new TeamSnapshot(
            state.TeamId,
            state.Name,
            Roster(state.Members.Values),
            items,
            state.EndorsementTypes.ToList(),
            state.PointDescriptions,
            state.MaxEndorsements);
    }

    /// <summary>The tx entry for an event, or null when the event has no wire form.</summary>
    public static WireEvent? ToWire(TeamEvent evt) {
        return evt switch {
            TeamEvent.ActionItemAssigned e => new TeamActionUpdated(e.Item),
            TeamEvent.ActionItemDoneSet e => new TeamActionUpdated(e.Item),
            TeamEvent.ActionItemSourceDetached e => new TeamActionUpdated(e.Item),
            TeamEvent.ActionItemAdded e => new TeamActionAdded(e.Item),
            TeamEvent.ActionItemDeleted e => new TeamActionDeleted(e.ItemId),
            TeamEvent.ActionItemTextSynced e => new TeamActionText(e.ItemId, e.Text, e.SourceVersion),
            TeamEvent.ActionItemCommentsCounted e => new TeamActionComments(e.ItemId, e.Count),
            TeamEvent.MemberAdded e => new TeamMembers(e.Members),
            TeamEvent.MemberRoleSet e => new TeamMembers(e.Members),
            TeamEvent.MemberRemoved e => new TeamMembers(e.Members),
            TeamEvent.BoardsChanged e => new TeamBoardsChanged(e.BoardId),
            TeamEvent.PokerChanged e => new TeamPokerChanged(e.SessionId, e.Pointed, e.Total),
            TeamEvent.SettingsChanged e => new TeamSettingsChanged(e.PointDescriptions, e.MaxEndorsements),
            TeamEvent.TeamDeleted => null,
            _ => throw new ArgumentOutOfRangeException(nameof(evt), evt.GetType().Name, "Unknown team event."),
        };
    }

    /// <summary>Owner first, then admins, then members; by name within a role.</summary>
    public static IReadOnlyList<MemberInfo> Roster(IEnumerable<MemberInfo> members) {
        return members
            .OrderBy(x => RoleRank(x.Role))
            .ThenBy(x => x.Name, NaturalCompare.Instance)
            .ThenBy(x => x.Pid.Value)
            .ToList();
    }

    private static int RoleRank(TeamRole role) => role switch {
        TeamRole.Owner => 0,
        TeamRole.Admin => 1,
        _ => 2,
    };
}
