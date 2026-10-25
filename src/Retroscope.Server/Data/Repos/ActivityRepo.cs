using System.Collections.Concurrent;
using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Room;

namespace Retroscope.Server.Data.Repos;

/// <summary>Told when a subscription to a room is admitted, so the room's team counts as visited (UX18 decision 8).</summary>
public interface IRoomVisits {
    /// <summary>Called on the room actor's thread: never blocks and never throws.</summary>
    void Visited(Principal who, TeamId team);
}

/// <summary>
/// <c>recent_activity</c> (§c.5): the teams a user opened, written by the server only and debounced in memory to once
/// per 5 minutes per (user, team). <c>me.lastTeamId</c> reads the latest one. Opening the team page and subscribing to
/// any room of the team both count; only <c>team</c> rows are written (UX18).
/// </summary>
public sealed partial class ActivityRepo(NpgsqlDataSource dataSource, TimeProvider time, ILogger<ActivityRepo> logger) : IRoomVisits {
    public static readonly TimeSpan Debounce = TimeSpan.FromMinutes(5);

    private const int MaxRemembered = 65_536;

    private readonly ConcurrentDictionary<(PrincipalId, TeamId), DateTimeOffset> _written = new();

    /// <summary>Records that <paramref name="user"/> opened the team, at most once per <see cref="Debounce"/>.</summary>
    public async Task TouchAsync(PrincipalId user, TeamId team, CancellationToken cancellationToken) {
        if (Due(user, team) is { } now) {
            await WriteAsync(user, team, now, cancellationToken);
        }
    }

    public void Visited(Principal who, TeamId team) {
        // Only users have activity rows; a room admits nobody else (UX18).
        if (who.Kind != PrincipalKind.User || Due(who.Id, team) is not { } now) {
            return;
        }
        _ = Task.Run(() => WriteInBackgroundAsync(who.Id, team, now));
    }

    private DateTimeOffset? Due(PrincipalId user, TeamId team) {
        var now = time.GetUtcNow();
        var key = (user, team);
        if (_written.TryGetValue(key, out var last) && now - last < Debounce) {
            return null;
        }
        if (_written.Count >= MaxRemembered) {
            _written.Clear();
        }
        _written[key] = now;
        return now;
    }

    private async Task WriteInBackgroundAsync(PrincipalId user, TeamId team, DateTimeOffset now) {
        try {
            await WriteAsync(user, team, now, CancellationToken.None);
        } catch (Exception e) {
            // A visit only steers where '/' lands; forgetting it lets the next visit try again.
            _written.TryRemove(new KeyValuePair<(PrincipalId, TeamId), DateTimeOffset>((user, team), now));
            LogTouchFailed(logger, e, team);
        }
    }

    private async Task WriteAsync(PrincipalId user, TeamId team, DateTimeOffset now, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand("""
            insert into recent_activity (user_id, entity_kind, entity_id, last_at) values ($1, 'team', $2, $3)
            on conflict (user_id, entity_kind, entity_id) do update set last_at = greatest(recent_activity.last_at, excluded.last_at)
            """);
        command.Parameters.Add(new() { Value = user.Value });
        command.Parameters.Add(new() { Value = team.Value });
        command.Parameters.Add(new() { Value = now });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [LoggerMessage(Level = LogLevel.Warning, Message = "Recording a visit to team {Team} failed")]
    private static partial void LogTouchFailed(ILogger logger, Exception exception, TeamId team);
}
