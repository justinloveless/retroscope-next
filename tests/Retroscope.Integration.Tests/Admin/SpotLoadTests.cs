using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Retroscope.Core.Ids;
using Retroscope.Integration.Tests.Leadership;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Data;
using Retroscope.Server.Hosting;
using static Retroscope.Integration.Tests.Admin.AdminHost;

namespace Retroscope.Integration.Tests.Admin;

[Trait("Category", "Admin")]
[Collection(SharedPostgres.Name)]
public sealed class SpotLoadTests(PostgresFixture postgres) {
    [Fact]
    public async Task Every_sampled_room_loads_and_projects_for_an_anonymous_viewer() {
        await using var host = await StartAsync(postgres);
        await SeedAsync(host);
        var output = new StringWriter();

        var report = await SpotLoadCommand.RunAsync(host.App.Services, 20, output, Token);

        Assert.True(report.Ok, output.ToString());
        Assert.Equal(1, report.Loaded[RoomKind.Poker]);
        Assert.Equal(1, report.Loaded[RoomKind.Board]);
        Assert.Equal(1, report.Loaded[RoomKind.Team]);
        Assert.Equal("spot-load: poker 1, board 1, team 1 loaded; 0 failed", output.ToString().Trim());
    }

    [Fact]
    public async Task The_sample_bounds_how_many_rooms_of_each_kind_are_loaded() {
        await using var host = await StartAsync(postgres);
        var team = await AdminEndpointTests.TeamAsync(host, "Team", (await host.CreateUserAsync("u@example.com")).Id);
        for (var i = 0; i < 5; i++) {
            await RetentionTests.SessionAsync(host, $"MANY{i:D2}", host.Time.GetUtcNow(), team);
        }

        var report = await SpotLoadCommand.RunAsync(host.App.Services, 3, TextWriter.Null, Token);

        Assert.Equal(3, report.Loaded[RoomKind.Poker]);
    }

    [Theory]
    [InlineData(new[] { "spot-load" }, true, 20)]
    [InlineData(new[] { "spot-load", "--sample", "5" }, true, 5)]
    [InlineData(new[] { "spot-load", "--sample" }, false, 0)]
    [InlineData(new[] { "spot-load", "--sample", "0" }, false, 0)]
    [InlineData(new[] { "spot-load", "--sample", "x" }, false, 0)]
    [InlineData(new[] { "spot-load", "--sample", "5", "extra" }, false, 0)]
    [InlineData(new[] { "spot-load", "--all" }, false, 0)]
    public void Arguments_are_parsed_strictly(string[] args, bool ok, int sample) {
        Assert.Equal(ok, SpotLoadCommand.TryParseSample(args, out var parsed));
        if (ok) {
            Assert.Equal(sample, parsed);
        }
    }

    [Fact]
    public async Task The_server_binary_runs_spot_load_and_exits_zero() {
        var host = await StartAsync(postgres);
        await SeedAsync(host);
        await host.DisposeAsync();

        var (exitCode, output) = await RunBinaryAsync(host.Database, "spot-load", "--sample", "5");

        Assert.True(exitCode == 0, output);
        Assert.Contains("spot-load: poker 1, board 1, team 1 loaded; 0 failed", output, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_server_binary_refuses_bad_arguments_and_a_running_app() {
        var database = await postgres.CreateDatabaseAsync();

        var (usage, usageOutput) = await RunBinaryAsync(database, "spot-load", "--sample", "lots");
        Assert.Equal(2, usage);
        Assert.Contains("usage: retroscope spot-load", usageOutput, StringComparison.Ordinal);

        await using var running = LeaderHarness.Create(database);
        await running.StartAsync(Token);
        var (busy, busyOutput) = await RunBinaryAsync(database, "spot-load");
        Assert.Equal(1, busy);
        Assert.Contains("Stop the app before running spot-load", busyOutput, StringComparison.Ordinal);
    }

    // A team with a member and an action item, its session with a voted round and its board with a named and an
    // anonymous card; plus rooms that must not be sampled: a deleted board, a soft-deleted team's session and a
    // leftover team-less session.
    private static async Task SeedAsync(AdminHost host) {
        var user = await host.CreateUserAsync("u@example.com");
        var now = host.Time.GetUtcNow();
        var team = await AdminEndpointTests.TeamAsync(host, "Spot team", user.Id);
        var session = await RetentionTests.SessionAsync(host, "SPOT01", now, team);
        var round = Guid.CreateVersion7();
        await host.ExecuteAsync(
            "insert into poker_rounds (id, session_id, number, sort_key, ticket_key) values ($1, $2, 1, 0, 'ABC-1')", round, session.Value);
        await host.ExecuteAsync(
            "insert into poker_votes (round_id, principal_id, points) values ($1, $2, 5)", round, user.Id.Value);

        var board = await RetentionTests.BoardAsync(host, "SPOTB1", now, team);
        var column = Guid.CreateVersion7();
        await host.ExecuteAsync(
            "insert into retro_columns (id, board_id, title, color, sort_key) values ($1, $2, 'Good', 'green', 0)", column, board.Value);
        await host.ExecuteAsync(
            "insert into retro_cards (id, board_id, column_id, body, author_id) values ($1, $2, $3, 'Named **card**', $4)",
            Guid.CreateVersion7(), board.Value, column, user.Id.Value);
        await host.ExecuteAsync(
            "insert into retro_cards (id, board_id, column_id, body, anon_tag) values ($1, $2, $3, 'Anonymous card', $4)",
            Guid.CreateVersion7(), board.Value, column, RandomNumberGenerator.GetBytes(32));
        var deletedBoard = await RetentionTests.BoardAsync(host, "SPOTB2", now, team);
        await host.ExecuteAsync("update retro_boards set deleted_at = now() where id = $1", deletedBoard.Value);

        await host.ExecuteAsync(
            "insert into team_action_items (id, team_id, text) values ($1, $2, 'Follow up')", Guid.CreateVersion7(), team.Value);
        var gone = await AdminEndpointTests.TeamAsync(host, "Gone", user.Id, deleted: true);
        await RetentionTests.SessionAsync(host, "SPOT02", now, gone);
        await RetentionTests.SessionAsync(host, "SPOT03", now);
    }

    private static async Task<(int ExitCode, string Output)> RunBinaryAsync(TestDatabase database, params string[] args) {
        var binary = Path.Combine(AppContext.BaseDirectory, OperatingSystem.IsWindows() ? "retroscope.exe" : "retroscope");
        var info = new ProcessStartInfo(binary) {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            WorkingDirectory = AppContext.BaseDirectory,
        };
        foreach (var arg in args) {
            info.ArgumentList.Add(arg);
        }
        info.Environment["DOTNET_hostBuilder__reloadConfigOnChange"] = "false";
        info.Environment["PUBLIC_URL"] = "http://127.0.0.1";
        info.Environment["DATABASE_URL"] = database.AppConnectionString;
        info.Environment["DATABASE_OWNER_URL"] = database.OwnerConnectionString;
        info.Environment["ANON_TAG_KEY"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        info.Environment[LeaderOptions.AcquireSecondsKey] = "2";
        var output = new StringBuilder();
        using var process = new Process { StartInfo = info };
        process.OutputDataReceived += (_, e) => Append(e.Data);
        process.ErrorDataReceived += (_, e) => Append(e.Data);
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try {
            await process.WaitForExitAsync(timeout.Token);
        } catch (OperationCanceledException) {
            process.Kill(entireProcessTree: true);
            throw;
        }
        process.WaitForExit();
        lock (output) {
            return (process.ExitCode, output.ToString());
        }

        void Append(string? line) {
            if (line is null) {
                return;
            }
            lock (output) {
                output.AppendLine(line);
            }
        }
    }
}
