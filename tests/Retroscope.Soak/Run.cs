using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Retroscope.Soak;

internal sealed record Converged(string After, JsonElement Result) {
    public bool Ok => Result.GetProperty("ok").GetBoolean() && ConvergedMs <= Run.ConvergenceBudget.TotalMilliseconds;

    public long ConvergedMs => Result.GetProperty("convergedMs").GetInt64();

    public long ReconnectMs => Result.GetProperty("reconnectMs").GetInt64();

    /// <summary>The verifier probes that needed more than one attempt, each with why its attempts failed.</summary>
    public JsonNode? RetriedProbes => Result.TryGetProperty("retriedProbes", out var probes) && probes.GetArrayLength() > 0
        ? JsonNode.Parse(probes.GetRawText())
        : null;
}

/// <summary>
/// One soak run (DESIGN §j): bring the stack up, start the clients, run the load for the requested time while the
/// chaos schedule kills the app every <see cref="Profile.KillEvery"/>, restarts the database once, redeploys the app
/// once in the middle of a vote and partitions the clients; check convergence after every heal, then judge the run.
/// </summary>
internal sealed class Run(Profile profile, TimeSpan duration, Stack stack, string root, string output) {
    public static readonly TimeSpan ConvergenceBudget = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan RedeployReconnectBudget = TimeSpan.FromSeconds(15);
    public const long RssBudgetBytes = 160L << 20;

    private readonly List<Outage> _outages = [];
    private readonly List<Converged> _convergence = [];
    private readonly Stopwatch _clock = new();
    private Outage? _redeploy;

    public async Task<Report> ExecuteAsync() {
        Log($"stack up ({Stack.Project}, https://localhost:{stack.HttpsPort})");
        await stack.DownAsync();
        await stack.UpAsync();
        using var toxiproxy = new Toxiproxy(stack.ToxiproxyPort);
        await toxiproxy.CreateAsync();
        using var monitor = new AppMonitor(stack.BaseUrl);
        await monitor.SignInAsAdminAsync(stack);
        var owners = await Owners.SeedAsync(stack, profile);
        Log($"seeded {owners.Count} team owners");

        var resultsPath = Path.Combine(output, "client-results.json");
        await using var clients = ClientHost.Start(root, stack.BaseUrl, new JsonObject {
            ["rooms"] = new JsonObject {
                ["poker"] = profile.PokerRooms,
                ["retro"] = profile.RetroRooms,
                ["spotlight"] = profile.SpotlightRooms,
                ["clients"] = profile.ClientsPerRoom,
            },
            ["storm"] = new JsonObject { ["boards"] = profile.StormBoards, ["cards"] = profile.StormCards, ["creators"] = profile.StormCreators },
            ["rate"] = profile.CommandsPerSecond,
            ["owners"] = owners,
            ["resultsPath"] = resultsPath,
        });
        var ready = await clients.WaitForAsync("ready", TimeSpan.FromMinutes(20));
        Log($"clients ready: {ready}");

        using var stop = new CancellationTokenSource();
        var sampler = monitor.RunAsync(stop.Token);
        await toxiproxy.AddLatencyAsync(150, 100);
        var resets = ResetLoopAsync(toxiproxy, stop.Token);
        await clients.AskAsync("resume", TimeSpan.FromSeconds(10));
        _clock.Start();

        foreach (var step in Schedule()) {
            var wait = step.At - _clock.Elapsed;
            if (wait > TimeSpan.Zero) {
                await Task.Delay(wait);
            }
            Log($"{step.Name}");
            await step.Act(clients, toxiproxy);
        }
        var remaining = duration - _clock.Elapsed;
        if (remaining > TimeSpan.Zero) {
            await Task.Delay(remaining);
        }

        Log("stopping the load and checking the final state");
        var stopped = await clients.AskAsync("stop", TimeSpan.FromMinutes(5));
        _convergence.Add(new Converged("end", stopped.GetProperty("final")));
        var runEnd = DateTimeOffset.UtcNow;
        await stop.CancelAsync();
        await Task.WhenAll(Quietly(sampler), Quietly(resets));

        var results = JsonSerializer.Deserialize<ClientResults>(await File.ReadAllTextAsync(resultsPath), ClientResults.Json)!;
        var sessions = results.Rooms.Where(r => r.Kind == "poker").Select(r => r.Id).ToList();
        var boards = results.Rooms.Where(r => r.Kind == "board").Select(r => r.Id).ToList();
        var rows = await Rows.LoadAsync(stack, sessions, boards);
        var findings = Oracle.Check(results, rows, _outages, runEnd);
        return Report.Build(profile, duration, results, rows, findings, _convergence, _outages, _redeploy, clients.Reconnects, monitor.Samples);
    }

    private sealed record Step(TimeSpan At, string Name, Func<ClientHost, Toxiproxy, Task> Act);

    private IEnumerable<Step> Schedule() {
        var steps = new List<Step>();
        var end = duration - TimeSpan.FromSeconds(60);
        for (var at = profile.BurstEvery; at < duration; at += profile.BurstEvery) {
            steps.Add(new Step(at, "burst", (c, _) => c.AskAsync("burst", TimeSpan.FromSeconds(10))));
        }
        var kills = new List<TimeSpan>();
        for (var at = profile.KillEvery; at < end; at += profile.KillEvery) {
            kills.Add(at);
            steps.Add(new Step(at, "kill -9 app", (c, _) => OutageAsync("kill", c, stack.KillAppAsync)));
        }
        var dbRestart = duration * 0.42;
        steps.Add(new Step(dbRestart, "restart db", (c, _) => OutageAsync("db restart", c, stack.RestartDbAsync)));
        // Mid-vote: half a second into the burst nearest 60 % of the run, while votes and locks are in flight.
        var redeployBurst = TimeSpan.FromTicks(
            (long)Math.Round(duration * 0.6 / profile.BurstEvery) * profile.BurstEvery.Ticks);
        steps.Add(new Step(redeployBurst + TimeSpan.FromMilliseconds(500), "redeploy app mid-vote", (c, _) => RedeployAsync(c)));
        var busy = kills.Append(dbRestart).Append(redeployBurst).ToList();
        for (var at = profile.KillEvery / 2; at < end; at += profile.KillEvery) {
            var start = at;
            if (busy.All(b => (b - start).Duration() > TimeSpan.FromSeconds(30))) {
                steps.Add(new Step(start, $"partition {profile.PartitionLength.TotalSeconds:0} s", (c, t) => PartitionAsync(c, t)));
            }
        }
        return steps.OrderBy(s => s.At).ThenBy(s => s.Name == "burst" ? 0 : 1);
    }

    private async Task OutageAsync(string name, ClientHost clients, Func<Task> act) {
        var from = DateTimeOffset.UtcNow;
        await act();
        _outages.Add(new Outage(name, from, DateTimeOffset.UtcNow));
        await ConvergeAsync(name, clients);
    }

    private async Task RedeployAsync(ClientHost clients) {
        var from = DateTimeOffset.UtcNow;
        await stack.RedeployAppAsync();
        _redeploy = new Outage("redeploy", from, DateTimeOffset.UtcNow);
        _outages.Add(_redeploy);
        await ConvergeAsync("redeploy", clients);
    }

    private async Task PartitionAsync(ClientHost clients, Toxiproxy toxiproxy) {
        await toxiproxy.SetEnabledAsync(false);
        await Task.Delay(profile.PartitionLength);
        await toxiproxy.SetEnabledAsync(true);
        await ConvergeAsync("partition", clients);
    }

    /// <summary>
    /// After a heal: the load pauses, every tab must agree with a fresh snapshot, then the load resumes.
    /// </summary>
    private async Task ConvergeAsync(string after, ClientHost clients) {
        var converged = new Converged(after, await clients.AskAsync("converge", TimeSpan.FromMinutes(5)));
        _convergence.Add(converged);
        Log($"after {after}: back in {converged.ReconnectMs} ms, converged in {converged.ConvergedMs} ms{(converged.Ok ? "" : $" FAILED {converged.Result}")}");
        await clients.AskAsync("resume", TimeSpan.FromSeconds(10));
    }

    private static async Task ResetLoopAsync(Toxiproxy toxiproxy, CancellationToken cancellationToken) {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(10));
        while (await timer.WaitForNextTickAsync(cancellationToken)) {
            try {
                await toxiproxy.ResetSomeAsync(0.005);
            } catch (HttpRequestException e) {
                Console.Error.WriteLine($"toxiproxy reset failed: {e.Message}");
            }
        }
    }

    // The sampler and the reset loop only feed the report; a fault in either must not cost the run its verdict.
    private static async Task Quietly(Task task) {
        try {
            await task;
        } catch (OperationCanceledException) {
        } catch (Exception e) {
            Console.Error.WriteLine($"background task failed: {e}");
        }
    }

    private void Log(string message) => Console.WriteLine($"[soak {_clock.Elapsed:hh\\:mm\\:ss}] {message}");
}
