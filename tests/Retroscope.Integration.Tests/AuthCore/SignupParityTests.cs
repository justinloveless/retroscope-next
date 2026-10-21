using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using Retroscope.Integration.Tests.Auth;
using Retroscope.Integration.Tests.Schema;

namespace Retroscope.Integration.Tests.AuthCore;

// Signup answers 202 whether the address is free or taken; neither the database work nor the time it takes may tell
// the two apart.
[Trait("Category", "AuthCore")]
[Collection(SharedPostgres.Name)]
public sealed class SignupParityTests(PostgresFixture postgres) {
    private const string Password = "correct horse battery";

    [Fact]
    public async Task A_taken_address_runs_the_same_database_commands_as_a_new_one() {
        await using var host = await AuthHost.StartAsync(postgres);
        await host.CreateUserAsync("ada@example.com");
        using var commands = new CommandRecorder(host.Database.Name);

        var created = await commands.DuringAsync(() => SignupAsync(host, "grace@example.com", "198.51.100.1"));
        var taken = await commands.DuringAsync(() => SignupAsync(host, "ada@example.com", "198.51.100.2"));

        Assert.Single(created);
        Assert.Equal(created, taken);
        Assert.Equal(2L, await host.ScalarAsync<long>("select count(*) from users"));
        Assert.Equal(2L, await host.ScalarAsync<long>("select count(*) from principals"));
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from audit_log where action = 'auth.signup'"));
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from audit_log where action = 'auth.signup.taken'"));
    }

    [Fact]
    public async Task A_taken_address_costs_as_much_time_as_a_new_one() {
        await using var host = await AuthHost.StartAsync(postgres);
        await host.CreateUserAsync("ada@example.com");

        var created = new List<double>();
        var taken = new List<double>();
        for (var i = 0; i < 5; i++) {
            created.Add(await TimeAsync(() => SignupAsync(host, $"new{i}@example.com", $"198.51.100.{i}")));
            taken.Add(await TimeAsync(() => SignupAsync(host, "ada@example.com", $"198.51.101.{i}")));
        }

        var createdMedian = created.Order().ElementAt(2);
        var takenMedian = taken.Order().ElementAt(2);
        Assert.True(
            takenMedian > createdMedian * 0.6 && createdMedian > takenMedian * 0.6,
            $"A new address took {createdMedian:F1} ms, a taken one {takenMedian:F1} ms: timing reveals the account.");
    }

    private static async Task SignupAsync(AuthHost host, string email, string peer) {
        using var response = await host.Browser(peer).PostAsync("/api/auth/signup", new { email, password = Password, fullName = "Ada" });
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
    }

    private static async Task<double> TimeAsync(Func<Task> action) {
        var stopwatch = Stopwatch.StartNew();
        await action();
        stopwatch.Stop();
        return stopwatch.Elapsed.TotalMilliseconds;
    }

    /// <summary>Every Npgsql command against one database (not the connection opens), from Npgsql's activity source.</summary>
    private sealed class CommandRecorder : IDisposable {
        private readonly ActivityListener _listener;
        private readonly ConcurrentQueue<string> _commands = new();

        public CommandRecorder(string database) {
            _listener = new ActivityListener {
                ShouldListenTo = source => source.Name == "Npgsql",
                Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
                ActivityStopped = activity => {
                    if (!activity.DisplayName.StartsWith("CONNECT", StringComparison.Ordinal)
                        && activity.TagObjects.Any(x => x.Value is string value && value == database)) {
                        _commands.Enqueue(activity.DisplayName);
                    }
                },
            };
            ActivitySource.AddActivityListener(_listener);
        }

        public async Task<List<string>> DuringAsync(Func<Task> action) {
            _commands.Clear();
            await action();
            return [.. _commands];
        }

        public void Dispose() => _listener.Dispose();
    }
}
