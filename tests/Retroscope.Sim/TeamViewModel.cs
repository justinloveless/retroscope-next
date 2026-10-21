using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;

namespace Retroscope.Sim;

/// <summary>
/// The client-side fold of a team room (the rules U30's team model follows): start from the snapshot and apply each
/// tx event. Its view must equal a fresh <c>TeamProjection.Snapshot</c>.
/// </summary>
internal sealed class TeamViewModel(TeamSnapshot snapshot) {
    private readonly Dictionary<ActionItemId, ActionItemInfo> _items = snapshot.ActionItems.ToDictionary(x => x.Id);
    private TeamSnapshot _snapshot = snapshot;

    public IReadOnlyCollection<ActionItemInfo> Items => _items.Values;

    public IReadOnlyList<MemberInfo> Members => _snapshot.Members;

    public void Apply(WireEvent evt) {
        switch (evt) {
            case TeamActionAdded added:
                _items[added.Item.Id] = added.Item;
                break;
            case TeamActionUpdated updated:
                _items[updated.Item.Id] = updated.Item;
                break;
            case TeamActionText text when _items.TryGetValue(text.ItemId, out var item):
                _items[text.ItemId] = item with { Text = text.Text, SourceVersion = text.SourceVersion };
                break;
            case TeamActionComments comments when _items.TryGetValue(comments.ItemId, out var item):
                _items[comments.ItemId] = item with { CommentCount = comments.Count };
                break;
            case TeamActionDeleted deleted:
                _items.Remove(deleted.ItemId);
                break;
            case TeamMembers members:
                _snapshot = _snapshot with { Members = members.Members };
                break;
            case TeamSettingsChanged settings:
                _snapshot = _snapshot with {
                    PointDescriptions = settings.PointDescriptions ?? _snapshot.PointDescriptions,
                    MaxEndorsements = settings.MaxEndorsements ?? _snapshot.MaxEndorsements,
                };
                break;
            case TeamActionText or TeamActionComments or TeamBoardsChanged or TeamPokerChanged:
                break;
            default:
                throw new InvalidOperationException($"The team model has no rule for {evt.GetType().Name}.");
        }
    }

    /// <summary>The folded state in the server's canonical order: newest item first, then by id.</summary>
    public TeamSnapshot View() {
        return _snapshot with {
            ActionItems = [.. _items.Values.OrderByDescending(x => x.CreatedAt).ThenBy(x => x.Id.Value)],
        };
    }
}
