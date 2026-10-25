using System.Collections.Concurrent;
using Npgsql;
using Retroscope.Core.Ids;

namespace Retroscope.Server.Data.Repos;

public enum ActivityKind : byte {
    Team,
    Board,
    Poker,
}

/// <summary>
/// <c>recent_activity</c> (§c.5), written by the server only and debounced in memory to once per 5 minutes per
/// (user, entity).
/// </summary>
public sealed class ActivityRepo(NpgsqlDataSource dataSource, TimeProvider time) {
    public static readonly TimeSpan Debounce = TimeSpan.FromMinutes(5);

    private const int MaxRemembered = 65_536;

    private readonly ConcurrentDictionary<(PrincipalId, ActivityKind, Guid), DateTimeOffset> _written = new();

    /// <summary>Records that <paramref name="user"/> opened the entity, at most once per <see cref="Debounce"/>.</summary>
    public async Task TouchAsync(PrincipalId user, ActivityKind kind, Guid entity, CancellationToken cancellationToken) {
        var now = time.GetUtcNow();
        var key = (user, kind, entity);
        if (_written.TryGetValue(key, out var last) && now - last < Debounce) {
            return;
        }
        if (_written.Count >= MaxRemembered) {
            _written.Clear();
        }
        _written[key] = now;
        await using var command = dataSource.CreateCommand("""
            insert into recent_activity (user_id, entity_kind, entity_id, last_at) values ($1, $2, $3, $4)
            on conflict (user_id, entity_kind, entity_id) do update set last_at = greatest(recent_activity.last_at, excluded.last_at)
            """);
        command.Parameters.Add(new() { Value = user.Value });
        command.Parameters.Add(new() { Value = KindName(kind) });
        command.Parameters.Add(new() { Value = entity });
        command.Parameters.Add(new() { Value = now });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string KindName(ActivityKind kind) => kind switch {
        ActivityKind.Team => "team",
        ActivityKind.Board => "board",
        _ => "poker",
    };
}
