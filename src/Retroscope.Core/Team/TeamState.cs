using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;

namespace Retroscope.Core.Team;

/// <summary>
/// One team room's state, owned by its actor thread. Action items are the open ones plus those closed
/// recently; members mirror <c>team_members</c>, which only the team actor writes (R24).
/// </summary>
public sealed class TeamState {
    public TeamState(TeamId teamId, string name) {
        TeamId = teamId;
        Name = name;
    }

    public TeamId TeamId { get; }

    public string Name { get; set; }

    /// <summary>Set when the team is soft-deleted while its actor is live; every command is then refused.</summary>
    public bool Deleted { get; set; }

    public Dictionary<PrincipalId, MemberInfo> Members { get; } = [];

    public Dictionary<ActionItemId, ActionItemInfo> ActionItems { get; } = [];

    public List<EndorsementTypeInfo> EndorsementTypes { get; } = [];

    public IReadOnlyDictionary<string, string> PointDescriptions { get; set; } = new Dictionary<string, string>();

    public int MaxEndorsements { get; set; } = 3;

    public ActionItemInfo? FindBySourceCard(CardId card) {
        foreach (var item in ActionItems.Values) {
            if (item.SourceCardId == card) {
                return item;
            }
        }
        return null;
    }
}
