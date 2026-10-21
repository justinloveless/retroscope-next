using Retroscope.Core.Authz;
using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using Retroscope.Core.Room;
using Retroscope.Core.Team;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Realtime.Actors;
using Retroscope.Server.Realtime.Stores;

namespace Retroscope.Server.Realtime.Rooms;

public static class TeamRoom {
    public static void AddServices(IServiceCollection services, IConfiguration configuration) {
        services.AddSingleton<MembershipCache>();
        services.AddSingleton<IMembershipLookup>(sp => sp.GetRequiredService<MembershipCache>());
        services.AddSingleton<IAccessView>(sp => sp.GetRequiredService<MembershipCache>());
        services.AddSingleton<TeamStore>();
        // The registry is built from every room type, so anything a room type holds resolves it lazily.
        services.AddSingleton<ITeamNotices>(sp => new TeamNotices(() => sp.GetRequiredService<ActorRegistry>()));
        services.AddRoom(
            sp => new TeamBinding(sp.GetRequiredService<TimeProvider>()),
            sp => new TeamFanOut(
                sp.GetRequiredService<TeamStore>(),
                sp.GetRequiredService<MembershipCache>(),
                () => sp.GetRequiredService<ActorRegistry>(),
                sp.GetRequiredService<ILogger<TeamFanOut>>()));
    }

    public static void Map(IEndpointRouteBuilder app) {
    }
}

/// <summary>The team room on the actor runtime (§c.4 Team). Team rooms track no presence and have no overlay.</summary>
public sealed class TeamBinding(TimeProvider time) : IRoomBinding<TeamState, TeamInput, TeamEvent> {
    public RoomKind Kind => RoomKind.Team;

    public IRoomDomain<TeamState, TeamInput, TeamEvent> Domain => TeamDecider.Instance;

    public bool TracksPresence => false;

    public TeamInput? FromClient(Command command) => command is TeamCommand team ? TeamInput.From(team) : null;

    /// <summary>
    /// Events with nothing to write are sequenced without a transaction. Settings and deletion stay committed even
    /// though the HTTP handler wrote them: their fan-out runs after the commit (<see cref="TeamFanOut"/>).
    /// </summary>
    public EventDurability DurabilityOf(TeamEvent evt) => evt switch {
        TeamEvent.ActionItemAdded
            or TeamEvent.ActionItemSourceDetached
            or TeamEvent.ActionItemCommentsCounted
            or TeamEvent.BoardsChanged
            or TeamEvent.PokerChanged => EventDurability.Ephemeral,
        _ => EventDurability.Committed,
    };

    public bool CanSubscribe(TeamState state, Viewer viewer, IAccessView access) {
        return Policy.Can(viewer, Capability.Subscribe, TeamDecider.Facts(state), access);
    }

    public RoomSnapshot Snapshot(TeamState state, IReadOnlyList<PrincipalInfo> presence, bool forMembers) {
        return TeamProjection.Snapshot(state, time.GetUtcNow());
    }

    public RoomOverlay? Overlay(TeamState state, Viewer viewer) => null;

    public IReadOnlyList<WireEvent> Project(TeamState state, IReadOnlyList<TeamEvent> events, PrincipalId viewer, bool viewerIsMember) {
        return [.. events.Select(TeamProjection.ToWire).OfType<WireEvent>()];
    }

    public bool IsDeleted(TeamState state) => state.Deleted;
}

/// <summary>Board and poker actors' posts to the team actor: synchronous enqueues on live rooms only (§c.2).</summary>
public sealed class TeamNotices(Func<ActorRegistry> registry) : ITeamNotices {
    public void BoardsChanged(TeamId team, BoardId board) => Post(team, new TeamInput.BoardsChanged(board));

    public void PokerChanged(TeamId team, SessionId session, int pointed, int total) {
        Post(team, new TeamInput.PokerChanged(session, pointed, total));
    }

    public void ActionItemCommentsChanged(TeamId team, ActionItemId item, int count) {
        Post(team, new TeamInput.ActionItemCommentsChanged(item, count));
    }

    public void ActionItemCreated(TeamId team, ActionItemInfo item) => Post(team, new TeamInput.ActionItemCreated(item));

    public void ActionItemTextChanged(TeamId team, CardId sourceCard, string text, int cardVersion) {
        Post(team, new TeamInput.SyncActionText(sourceCard, text, cardVersion));
    }

    public void SourceCardDeleted(TeamId team, CardId sourceCard) => Post(team, new TeamInput.SourceCardDeleted(sourceCard));

    private void Post(TeamId team, TeamInput input) => registry().Post<TeamState, TeamInput>(RoomKey.Team(team), input);
}

/// <summary>
/// The team room's store: <see cref="TeamStore"/> plus what has to follow a committed membership or settings change
/// before the next message is decided anywhere (R12, R15c): the cache gets the new roster version, and the team's
/// live rooms get the roster, the settings and an access re-check.
/// </summary>
public sealed partial class TeamFanOut(
    TeamStore store,
    MembershipCache memberships,
    Func<ActorRegistry> registry,
    ILogger<TeamFanOut> logger) : IRoomStore<TeamState, TeamEvent> {
    public Task<TeamState?> LoadAsync(RoomKey room, DateTimeOffset now, CancellationToken cancellationToken) {
        return store.LoadAsync(room, now, cancellationToken);
    }

    public Task<ClientCursor> LoadCursorAsync(RoomKey room, ClientId client, PrincipalId principal, CancellationToken cancellationToken) {
        return store.LoadCursorAsync(room, client, principal, cancellationToken);
    }

    public async Task CommitAsync(
        RoomKey room,
        TeamState state,
        IReadOnlyList<TeamEvent> events,
        CursorAdvance? cursor,
        CancellationToken cancellationToken) {
        var team = new TeamId(room.Id);
        var roster = events.Select(Roster).LastOrDefault(x => x is not null);
        try {
            await store.CommitAsync(room, state, events, cursor, cancellationToken);
        } catch (CommitRejectedException) {
            throw;
        } catch when (roster is not null) {
            // The outcome may be unknown: the rows, not the cache, decide until the actor reloads and republishes.
            memberships.Forget(team);
            throw;
        }
        if (roster is not null) {
            memberships.Publish(team, TeamStore.Roles(roster));
        }
        var settings = events.OfType<TeamEvent.SettingsChanged>().LastOrDefault();
        if (roster is null && settings is null) {
            return;
        }
        try {
            await FanOutAsync(team, roster, settings, cancellationToken);
        } catch (Exception e) {
            // The change committed; rooms that missed it read it from the database when they next load.
            FanOutFailed(logger, e, room);
        }
    }

    private async Task FanOutAsync(
        TeamId team,
        IReadOnlyList<MemberInfo>? roster,
        TeamEvent.SettingsChanged? settings,
        CancellationToken cancellationToken) {
        var actors = registry();
        var rooms = await store.RoomsAsync(team, cancellationToken);
        if (roster is not null) {
            actors.AccessChanged(RoomKey.Team(team));
            var seats = roster.Select(x => new PrincipalInfo(x.Pid, PrincipalKind.User, x.Name, x.Avatar)).ToList();
            foreach (var session in rooms.Sessions) {
                actors.Post<PokerState, PokerInput>(RoomKey.Poker(session), new RosterChanged(seats));
                actors.AccessChanged(RoomKey.Poker(session));
            }
            foreach (var board in rooms.Boards) {
                actors.Post<RetroState, RetroInput>(RoomKey.Board(board), new RetroInput.MembersChanged(roster));
                actors.AccessChanged(RoomKey.Board(board));
            }
        }
        if (settings?.PointDescriptions is { } descriptions) {
            foreach (var session in rooms.Sessions) {
                actors.Post<PokerState, PokerInput>(RoomKey.Poker(session), new PointDescriptionsChanged(descriptions));
            }
        }
        if (settings?.MaxEndorsements is { } maxEndorsements) {
            foreach (var board in rooms.Boards) {
                actors.Post<RetroState, RetroInput>(RoomKey.Board(board), new RetroInput.TeamSettingsChanged(maxEndorsements));
            }
        }
    }

    private static IReadOnlyList<MemberInfo>? Roster(TeamEvent evt) => evt switch {
        TeamEvent.MemberAdded e => e.Members,
        TeamEvent.MemberRoleSet e => e.Members,
        TeamEvent.MemberRemoved e => e.Members,
        _ => null,
    };

    [LoggerMessage(1, LogLevel.Error, "Room {Room} committed a membership or settings change but could not fan it out")]
    private static partial void FanOutFailed(ILogger logger, Exception exception, RoomKey room);
}
