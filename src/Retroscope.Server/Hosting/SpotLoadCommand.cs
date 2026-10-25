using System.Globalization;
using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Core.Security;
using Retroscope.Server.Data;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Realtime.Actors;
using Retroscope.Server.Realtime.Rooms;
using Retroscope.Server.Realtime.Stores;

namespace Retroscope.Server.Hosting;

public sealed record SpotLoadFailure(RoomKey Room, string Error);

public sealed record SpotLoadReport(
    IReadOnlyDictionary<RoomKind, int> Loaded,
    IReadOnlyList<SpotLoadFailure> Failures) {
    public bool Ok => Failures.Count == 0;
}

/// <summary>
/// <c>retroscope spot-load [--sample N]</c> (§h.5): loads N random rooms of each kind through the real stores and
/// builds the <c>snap</c> frame an anonymous viewer would get, proving that imported rooms open. The operator runs it
/// after restoring the import and before <c>cutover</c>, with the app stopped: it takes the leader lease, because the
/// team store's load repairs action-item copies under the epoch fence. Exit code 0 when every sampled room loaded, 1
/// when one did not or the lease was not acquired, 2 on a usage error.
/// </summary>
public static class SpotLoadCommand {
    public const int DefaultSample = 20;

    private const string Usage = "usage: retroscope spot-load [--sample N]   (N from 1 to 10000, default 20)";

    // Rooms that should open: not deleted, and belonging to a team that is not soft-deleted. A room of a deleted team
    // loads as missing by design.
    private const string PokerSql = """
        select s.id from poker_sessions s join teams t on t.id = s.team_id
        where t.deleted_at is null order by random() limit $1
        """;

    private const string BoardSql = """
        select b.id from retro_boards b join teams t on t.id = b.team_id
        where b.deleted_at is null and t.deleted_at is null order by random() limit $1
        """;

    private const string TeamSql = "select id from teams where deleted_at is null order by random() limit $1";

    public static async Task<int> RunAsync(WebApplication app, string[] args) {
        if (!TryParseSample(args, out var sample)) {
            await Console.Error.WriteLineAsync(Usage);
            return 2;
        }
        var services = app.Services;
        var leader = services.GetRequiredService<Leader>();
        var acquired = !leader.IsLeader;
        try {
            if (acquired) {
                await leader.StartAsync(CancellationToken.None);
            }
        } catch (LeaderUnavailableException e) {
            await Console.Error.WriteLineAsync($"spot-load: {e.Message} Stop the app before running spot-load.");
            return 1;
        }
        try {
            var report = await RunAsync(services, sample, Console.Out, CancellationToken.None);
            return report.Ok ? 0 : 1;
        } finally {
            if (acquired) {
                await leader.StopAsync(CancellationToken.None);
            }
        }
    }

    /// <summary>Samples and loads; writes one line per failure and a summary to <paramref name="output"/>.</summary>
    public static async Task<SpotLoadReport> RunAsync(
        IServiceProvider services,
        int sample,
        TextWriter output,
        CancellationToken cancellationToken) {
        var dataSource = services.GetRequiredService<NpgsqlDataSource>();
        var time = services.GetRequiredService<TimeProvider>();
        var now = time.GetUtcNow();
        var viewer = new Viewer(
            new Principal(new PrincipalId(Guid.NewGuid()), PrincipalKind.Guest, "spot-load", null, IsAppAdmin: false),
            new ClientId(Guid.NewGuid()),
            new ConnectionId(Guid.NewGuid()));
        var tagger = services.GetRequiredService<IAnonTagger>();
        var retroStore = services.GetService<RetroStore>() ?? new RetroStore(
            dataSource,
            services.GetRequiredService<Leader>(),
            services.GetRequiredService<CursorRepo>(),
            null,
            services.GetRequiredService<ILogger<RetroStore>>());

        var kinds = new (RoomKind Kind, string Sql, Func<RoomKey, Task<byte[]?>> Open)[] {
            (RoomKind.Poker, PokerSql, key => OpenAsync(
                key, services.GetRequiredService<PokerStore>(), new PokerBinding(new PokerDomain()), viewer, now, cancellationToken)),
            (RoomKind.Board, BoardSql, key => OpenAsync(key, retroStore, new RetroBinding(tagger), viewer, now, cancellationToken)),
            (RoomKind.Team, TeamSql, key => OpenAsync(
                key, services.GetRequiredService<TeamStore>(), new TeamBinding(time), viewer, now, cancellationToken)),
        };

        var loaded = new Dictionary<RoomKind, int>();
        var failures = new List<SpotLoadFailure>();
        foreach (var (kind, sql, open) in kinds) {
            var count = 0;
            foreach (var id in await SampleAsync(dataSource, sql, sample, cancellationToken)) {
                var key = new RoomKey(kind, id);
                string? error;
                try {
                    error = await open(key) is { } frame ? Check(frame) : "the room did not load";
                } catch (Exception e) when (e is not OperationCanceledException) {
                    error = $"{e.GetType().Name}: {e.Message}";
                }
                if (error is null) {
                    count++;
                } else {
                    failures.Add(new SpotLoadFailure(key, error));
                    await output.WriteLineAsync($"FAIL {key}: {error}");
                }
            }
            loaded[kind] = count;
        }
        await output.WriteLineAsync(string.Create(
            CultureInfo.InvariantCulture,
            $"spot-load: poker {loaded[RoomKind.Poker]}, board {loaded[RoomKind.Board]}, team {loaded[RoomKind.Team]} loaded; {failures.Count} failed"));
        return new SpotLoadReport(loaded, failures);
    }

    internal static bool TryParseSample(string[] args, out int sample) {
        sample = DefaultSample;
        var rest = args.AsSpan(args is ["spot-load", ..] ? 1 : 0);
        if (rest.Length == 0) {
            return true;
        }
        return rest is ["--sample", var value]
            && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out sample)
            && sample is >= 1 and <= 10_000;
    }

    private static async Task<IReadOnlyList<Guid>> SampleAsync(
        NpgsqlDataSource dataSource,
        string sql,
        int sample,
        CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(new NpgsqlParameter<int> { TypedValue = sample });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var ids = new List<Guid>();
        while (await reader.ReadAsync(cancellationToken)) {
            ids.Add(reader.GetGuid(0));
        }
        return ids;
    }

    /// <returns>The viewer's <c>snap</c> frame, or null when the store found no room.</returns>
    private static async Task<byte[]?> OpenAsync<TState, TInput, TEvent>(
        RoomKey key,
        IRoomStore<TState, TEvent> store,
        IRoomBinding<TState, TInput, TEvent> binding,
        Viewer viewer,
        DateTimeOffset now,
        CancellationToken cancellationToken)
        where TState : class
        where TInput : class {
        if (await store.LoadAsync(key, now, cancellationToken) is not { } state) {
            return null;
        }
        var shared = FrameBytes.SharedSnapshot(key.Kind, binding.Snapshot(state, [], forMembers: false));
        return FrameBytes.Snap(key, new Epoch(0), 0, 0, shared, binding.Overlay(state, viewer));
    }

    /// <summary>The frame must parse back as the snapshot a client would receive.</summary>
    private static string? Check(byte[] frame) {
        var parsed = WireParser.ParseServer(frame);
        return parsed.Ok && parsed.Frame is SnapFrame ? null : $"the snapshot does not parse: {parsed.Message}";
    }
}
