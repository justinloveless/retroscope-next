using Retroscope.Core.Authz;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Core.Team;

/// <summary>The team room's domain (§c.4 Team): action items, the roster and team-wide notices.</summary>
public sealed class TeamDecider : IRoomDomain<TeamState, TeamInput, TeamEvent> {
    public static readonly TeamDecider Instance = new();

    public Decision<TeamEvent> Decide(TeamState state, TeamInput input, CommandContext context) {
        return Stamped(DecideInput(state, input, context), context.Now);
    }

    private static Decision<TeamEvent> DecideInput(TeamState state, TeamInput input, CommandContext context) {
        return input switch {
            TeamInput.Client client => DecideClient(state, client.Command, context),
            TeamInput.ActionItemCreated created => Created(state, created.Item),
            TeamInput.SyncActionText sync => SyncText(state, sync),
            TeamInput.SourceCardDeleted deleted => SourceDeleted(state, deleted.SourceCard),
            TeamInput.ActionItemCommentsChanged comments => CommentsChanged(state, comments),
            TeamInput.BoardsChanged boards => Accept(new TeamEvent.BoardsChanged(boards.BoardId)),
            TeamInput.PokerChanged poker => Accept(new TeamEvent.PokerChanged(poker.SessionId, poker.Pointed, poker.Total)),
            TeamInput.MemberAdded added => MemberAdded(state, added.Member),
            TeamInput.MemberRoleSet roleSet => MemberRoleSet(state, roleSet),
            TeamInput.MemberRemoved removed => MemberRemoved(state, removed.Member),
            TeamInput.SettingsChanged settings => SettingsChanged(state, settings),
            TeamInput.TeamDeleted => state.Deleted ? NoOp() : Accept(new TeamEvent.TeamDeleted()),
            _ => Reject(NackCode.Invalid),
        };
    }

    public void Apply(TeamState state, TeamEvent evt) => TeamApply.Apply(state, evt);

    public IReadOnlyList<TeamEvent> OnPresence(TeamState state, PresenceDelta delta, PresenceView presence, DateTimeOffset now) => [];

    public IReadOnlyList<TimerRequest<TeamInput>> Timers(TeamState state, PresenceView presence, DateTimeOffset now) => [];

    public IReadOnlyList<TeamEvent> OnWarm(TeamState state, PresenceView presence, DateTimeOffset now) => [];

    public static ResourceFacts Facts(TeamState state) {
        return new ResourceFacts(RoomKey.Team(state.TeamId), Team: state.TeamId, TeamDeleted: state.Deleted);
    }

    private static Decision<TeamEvent> DecideClient(TeamState state, TeamCommand command, CommandContext context) {
        var capability = command switch {
            TeamActionDelete => Capability.TeamDeleteActionItem,
            _ => Capability.TeamUpdateActionItem,
        };
        if (context.Viewer is not { } viewer || !Policy.Can(viewer, capability, Facts(state), context.Access)) {
            return Reject(NackCode.Forbidden);
        }
        return command switch {
            TeamActionAssign assign => Assign(state, assign, context),
            TeamActionDone done => Done(state, done, viewer.Who.Id, context.Now),
            TeamActionDelete delete => state.ActionItems.ContainsKey(delete.ItemId)
                ? Accept(new TeamEvent.ActionItemDeleted(delete.ItemId))
                : Reject(NackCode.NotFound),
            _ => Reject(NackCode.Invalid),
        };
    }

    private static Decision<TeamEvent> Assign(TeamState state, TeamActionAssign assign, CommandContext context) {
        if (!state.ActionItems.TryGetValue(assign.ItemId, out var item)) {
            return Reject(NackCode.NotFound);
        }
        if (assign.UserId is { } assignee && context.Access.RoleIn(state.TeamId, assignee) is null) {
            return Reject(NackCode.Invalid, "The assignee is not a team member.");
        }
        if (item.AssignedTo == assign.UserId) {
            return NoOp();
        }
        return Accept(new TeamEvent.ActionItemAssigned(item with { AssignedTo = assign.UserId }));
    }

    private static Decision<TeamEvent> Done(TeamState state, TeamActionDone done, PrincipalId by, DateTimeOffset now) {
        if (!state.ActionItems.TryGetValue(done.ItemId, out var item)) {
            return Reject(NackCode.NotFound);
        }
        if ((item.DoneAt is not null) == done.Done) {
            return NoOp();
        }
        var updated = done.Done
            ? item with { DoneAt = now.ToUnixTimeMilliseconds(), DoneBy = by }
            : item with { DoneAt = null, DoneBy = null };
        return Accept(new TeamEvent.ActionItemDoneSet(updated));
    }

    private static Decision<TeamEvent> Created(TeamState state, ActionItemInfo item) {
        // R24: a team actor reloaded after the board actor's insert already holds the row.
        if (state.ActionItems.ContainsKey(item.Id)
            || (item.SourceCardId is { } card && state.FindBySourceCard(card) is not null)) {
            return NoOp();
        }
        return Accept(new TeamEvent.ActionItemAdded(item));
    }

    private static Decision<TeamEvent> SyncText(TeamState state, TeamInput.SyncActionText sync) {
        if (state.FindBySourceCard(sync.SourceCard) is not { } item || sync.CardVersion <= item.SourceVersion) {
            return NoOp();
        }
        return Accept(new TeamEvent.ActionItemTextSynced(item.Id, sync.Text, sync.CardVersion));
    }

    private static Decision<TeamEvent> SourceDeleted(TeamState state, CardId card) {
        if (state.FindBySourceCard(card) is not { } item) {
            return NoOp();
        }
        return Accept(new TeamEvent.ActionItemSourceDetached(item with { SourceCardId = null }));
    }

    private static Decision<TeamEvent> CommentsChanged(TeamState state, TeamInput.ActionItemCommentsChanged comments) {
        if (comments.Count < 0) {
            return Reject(NackCode.Invalid);
        }
        if (!state.ActionItems.TryGetValue(comments.ItemId, out var item) || item.CommentCount == comments.Count) {
            return NoOp();
        }
        return Accept(new TeamEvent.ActionItemCommentsCounted(comments.ItemId, comments.Count));
    }

    private static Decision<TeamEvent> MemberAdded(TeamState state, MemberInfo member) {
        if (state.Members.ContainsKey(member.Pid)) {
            return NoOp();
        }
        var roster = state.Members.Values.Append(member);
        return Accept(new TeamEvent.MemberAdded(member, TeamProjection.Roster(roster)));
    }

    private static Decision<TeamEvent> MemberRoleSet(TeamState state, TeamInput.MemberRoleSet roleSet) {
        if (!state.Members.TryGetValue(roleSet.Member, out var member) || member.Role == roleSet.Role) {
            return NoOp();
        }
        var roster = state.Members.Values.Select(x => x.Pid == roleSet.Member ? x with { Role = roleSet.Role } : x);
        return Accept(new TeamEvent.MemberRoleSet(roleSet.Member, roleSet.Role, TeamProjection.Roster(roster)));
    }

    private static Decision<TeamEvent> MemberRemoved(TeamState state, PrincipalId removed) {
        if (!state.Members.ContainsKey(removed)) {
            return NoOp();
        }
        var roster = state.Members.Values.Where(x => x.Pid != removed);
        return Accept(new TeamEvent.MemberRemoved(removed, TeamProjection.Roster(roster)));
    }

    private static Decision<TeamEvent> SettingsChanged(TeamState state, TeamInput.SettingsChanged settings) {
        var pointDescriptions = settings.PointDescriptions is { } descriptions && !SameEntries(descriptions, state.PointDescriptions)
            ? descriptions
            : null;
        var maxEndorsements = settings.MaxEndorsements is { } max && max != state.MaxEndorsements ? max : (int?)null;
        if (pointDescriptions is null && maxEndorsements is null) {
            return NoOp();
        }
        return Accept(new TeamEvent.SettingsChanged(pointDescriptions, maxEndorsements));
    }

    private static bool SameEntries(IReadOnlyDictionary<string, string> a, IReadOnlyDictionary<string, string> b) {
        return a.Count == b.Count && a.All(x => b.TryGetValue(x.Key, out var value) && string.Equals(value, x.Value, StringComparison.Ordinal));
    }

    private static Decision<TeamEvent> Stamped(Decision<TeamEvent> decision, DateTimeOffset now) {
        return decision is Decision<TeamEvent>.Accept accept
            ? new Decision<TeamEvent>.Accept(accept.Events.Select(x => x with { At = now }).ToList())
            : decision;
    }

    private static Decision<TeamEvent> Accept(TeamEvent evt) => new Decision<TeamEvent>.Accept([evt]);

    private static Decision<TeamEvent> NoOp() => new Decision<TeamEvent>.NoOp();

    private static Decision<TeamEvent> Reject(NackCode code, string? detail = null) => new Decision<TeamEvent>.Reject(code, detail);
}
