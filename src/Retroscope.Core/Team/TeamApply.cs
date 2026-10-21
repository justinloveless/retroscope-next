using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;

namespace Retroscope.Core.Team;

/// <summary>Folds committed events into <see cref="TeamState"/>. Runs only after the transaction committed.</summary>
public static class TeamApply {
    public static void Apply(TeamState state, TeamEvent evt) {
        switch (evt) {
            case TeamEvent.ActionItemAssigned e:
                state.ActionItems[e.Item.Id] = e.Item;
                break;
            case TeamEvent.ActionItemDoneSet e:
                state.ActionItems[e.Item.Id] = e.Item;
                break;
            case TeamEvent.ActionItemAdded e:
                state.ActionItems[e.Item.Id] = e.Item;
                break;
            case TeamEvent.ActionItemSourceDetached e:
                state.ActionItems[e.Item.Id] = e.Item;
                break;
            case TeamEvent.ActionItemDeleted e:
                state.ActionItems.Remove(e.ItemId);
                break;
            case TeamEvent.ActionItemTextSynced e:
                Update(state, e.ItemId, x => x with { Text = e.Text, SourceVersion = e.SourceVersion });
                break;
            case TeamEvent.ActionItemCommentsCounted e:
                Update(state, e.ItemId, x => x with { CommentCount = e.Count });
                break;
            case TeamEvent.MemberAdded e:
                ReplaceMembers(state, e.Members);
                break;
            case TeamEvent.MemberRoleSet e:
                ReplaceMembers(state, e.Members);
                break;
            case TeamEvent.MemberRemoved e:
                ReplaceMembers(state, e.Members);
                break;
            case TeamEvent.SettingsChanged e:
                state.PointDescriptions = e.PointDescriptions ?? state.PointDescriptions;
                state.MaxEndorsements = e.MaxEndorsements ?? state.MaxEndorsements;
                break;
            case TeamEvent.TeamDeleted:
                state.Deleted = true;
                break;
            case TeamEvent.BoardsChanged or TeamEvent.PokerChanged:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(evt), evt.GetType().Name, "Unknown team event.");
        }
        PruneClosed(state, evt.At);
    }

    /// <summary>Items closed before the snapshot window leave the live state too, as they would on a reload.</summary>
    private static void PruneClosed(TeamState state, DateTimeOffset now) {
        // Millisecond arithmetic: an event built outside the decider carries the default time, which would underflow.
        var closedSince = now.ToUnixTimeMilliseconds() - (long)TeamProjection.ClosedItemWindow.TotalMilliseconds;
        foreach (var item in state.ActionItems.Values.Where(x => x.DoneAt < closedSince).ToList()) {
            state.ActionItems.Remove(item.Id);
        }
    }

    private static void Update(TeamState state, ActionItemId id, Func<ActionItemInfo, ActionItemInfo> change) {
        if (state.ActionItems.TryGetValue(id, out var item)) {
            state.ActionItems[id] = change(item);
        }
    }

    private static void ReplaceMembers(TeamState state, IReadOnlyList<MemberInfo> members) {
        state.Members.Clear();
        foreach (var member in members) {
            state.Members[member.Pid] = member;
        }
    }
}
