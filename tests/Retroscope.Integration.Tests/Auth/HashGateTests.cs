using System.Diagnostics;
using System.Net;
using Retroscope.Core.Security;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Auth;

namespace Retroscope.Integration.Tests.Auth;

[Trait("Category", "AuthCore")]
public sealed class HashGateTests {
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public void Defaults_are_width_one_two_second_wait_and_a_queue_of_fifty() {
        using var gate = new HashGate();

        Assert.Equal(1, gate.Width);
        Assert.Equal(TimeSpan.FromSeconds(2), HashGate.DefaultWait);
        Assert.Equal(50, HashGate.DefaultQueueCap);
    }

    [Fact]
    public async Task Width_one_never_runs_two_hashes_at_once() {
        using var gate = new HashGate(wait: TimeSpan.FromSeconds(30));
        var running = 0;
        var peak = 0;

        var runs = Enumerable.Range(0, 6).Select(_ => Task.Run(() => gate.RunAsync(() => {
            var now = Interlocked.Increment(ref running);
            InterlockedMax(ref peak, now);
            Thread.Sleep(20);
            Interlocked.Decrement(ref running);
            return true;
        }, Token), Token));
        var results = await Task.WhenAll(runs);

        Assert.All(results, x => Assert.True(x.Admitted));
        Assert.Equal(1, peak);
    }

    [Fact]
    public async Task A_caller_that_waits_longer_than_the_limit_is_turned_away() {
        using var gate = new HashGate(wait: TimeSpan.FromMilliseconds(200));
        using var release = new ManualResetEventSlim();
        var holder = HoldAsync(gate, release);
        await WaitUntilAsync(() => Held);

        var stopwatch = Stopwatch.StartNew();
        var refused = await gate.RunAsync(() => true, Token);
        stopwatch.Stop();
        release.Set();
        await holder;

        Assert.False(refused.Admitted);
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromMilliseconds(150), TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Callers_beyond_the_queue_cap_are_turned_away_at_once() {
        using var gate = new HashGate(wait: TimeSpan.FromSeconds(30), queueCap: 2);
        using var release = new ManualResetEventSlim();
        var holder = HoldAsync(gate, release);
        await WaitUntilAsync(() => Held);
        var queued = new[] {
            Task.Run(() => gate.RunAsync(() => true, Token), Token),
            Task.Run(() => gate.RunAsync(() => true, Token), Token),
        };
        await WaitUntilAsync(() => gate.Waiting == 2);

        var stopwatch = Stopwatch.StartNew();
        var refused = await gate.RunAsync(() => true, Token);
        stopwatch.Stop();
        release.Set();
        await holder;

        Assert.False(refused.Admitted);
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(1));
        Assert.All(await Task.WhenAll(queued), x => Assert.True(x.Admitted));
    }

    private volatile bool _held;

    private bool Held => _held;

    private Task<Gated<bool>> HoldAsync(HashGate gate, ManualResetEventSlim release) =>
        Task.Run(() => gate.RunAsync(() => {
            _held = true;
            release.Wait(Token);
            return true;
        }, Token), Token);

    private static void InterlockedMax(ref int target, int value) {
        var current = Volatile.Read(ref target);
        while (value > current) {
            var seen = Interlocked.CompareExchange(ref target, value, current);
            if (seen == current) {
                return;
            }
            current = seen;
        }
    }

    internal static async Task WaitUntilAsync(Func<bool> condition) {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!condition()) {
            Assert.True(DateTime.UtcNow < deadline, "The condition did not hold in time.");
            await Task.Delay(10, Token);
        }
    }
}

[Trait("Category", "AuthCore")]
[Collection(SharedPostgres.Name)]
public sealed class HashGateEndpointTests(PostgresFixture postgres) {
    [Fact]
    public async Task Login_answers_503_busy_with_retry_after_when_the_gate_stays_full_for_two_seconds() {
        await using var host = await AuthHost.StartAsync(postgres);
        await host.CreateUserAsync("ada@example.com");
        var gate = host.Service<HashGate>();
        using var release = new ManualResetEventSlim();
        using var held = new ManualResetEventSlim();
        var holder = Task.Run(() => gate.RunAsync(() => {
            held.Set();
            return release.Wait(TimeSpan.FromSeconds(30), AuthHost.Token);
        }, AuthHost.Token), AuthHost.Token);
        await HashGateTests.WaitUntilAsync(() => held.IsSet);

        var stopwatch = Stopwatch.StartNew();
        using var response = await host.Browser().LoginAsync("ada@example.com", AuthHost.ImportedPassword);
        stopwatch.Stop();
        release.Set();
        await holder;

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("busy", await response.ProblemCodeAsync());
        Assert.Equal("2", response.Headers.GetValues("Retry-After").Single());
        Assert.InRange(stopwatch.Elapsed, TimeSpan.FromSeconds(1.9), TimeSpan.FromSeconds(10));
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from audit_log"));

        using var after = await host.Browser().LoginAsync("ada@example.com", AuthHost.ImportedPassword);
        Assert.Equal(HttpStatusCode.OK, after.StatusCode);
    }

    [Fact]
    public void Bcrypt_throughput_is_recorded() {
        var stopwatch = Stopwatch.StartNew();
        var hashes = 0;
        while (stopwatch.Elapsed < TimeSpan.FromSeconds(1) || hashes < 3) {
            Bcrypt.Hash("correct horse battery");
            hashes++;
        }
        stopwatch.Stop();
        var perSecond = hashes / stopwatch.Elapsed.TotalSeconds;

        TestContext.Current.SendDiagnosticMessage($"bcrypt cost {Bcrypt.WorkFactor}: {perSecond:F1} hashes/s on {Environment.ProcessorCount} cores ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture})");
        Console.WriteLine($"bcrypt cost {Bcrypt.WorkFactor}: {perSecond:F1} hashes/s");
        Assert.True(perSecond > 0);
    }
}
