using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Security;
using Retroscope.Fakes.FakeJira;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Data;
using Retroscope.Server.Jira;

namespace Retroscope.Integration.Tests.Jira.Client;

/// <summary>Answers from a table; unknown hosts resolve to the fake's loopback address. Records every lookup.</summary>
internal sealed class FakeResolver : IJiraResolver {
    private readonly ConcurrentDictionary<string, Queue<IPAddress[]>> _answers = new(StringComparer.Ordinal);

    public ConcurrentQueue<string> Lookups { get; } = new();

    public void Answer(string host, params IPAddress[][] answers) => _answers[host] = new Queue<IPAddress[]>(answers);

    public ValueTask<IPAddress[]> ResolveAsync(string host, CancellationToken cancellationToken) {
        Lookups.Enqueue(host);
        if (_answers.TryGetValue(host, out var queue)) {
            lock (queue) {
                if (queue.Count > 1) {
                    return ValueTask.FromResult(queue.Dequeue());
                }
                if (queue.Count == 1) {
                    return ValueTask.FromResult(queue.Peek());
                }
            }
        }
        return ValueTask.FromResult(new[] { IPAddress.Loopback });
    }
}

/// <summary>
/// A JiraClient wired to FakeJira over loopback, a migrated database holding one configured team, a fake clock and
/// a delay function that records each wait and advances the clock instead of sleeping.
/// </summary>
internal sealed class JiraHarness : IAsyncDisposable {
    public const string SiteHost = "acme.atlassian.net";

    // The fake listens on 127.0.0.1 (and refuses on ::1), so tests allow exactly those; every other address goes
    // through the production rule.
    public static readonly Func<IPAddress, bool> AllowFakeOrPublic =
        a => a.Equals(IPAddress.Loopback) || a.Equals(IPAddress.IPv6Loopback) || IpClassifier.IsPublic(a);

    private JiraHarness(
        FakeJiraServer jira,
        NpgsqlDataSource dataSource,
        Keyring keyring,
        Credentials credentials,
        JiraThrottle throttle,
        JiraCache cache,
        JiraClient client,
        FakeTimeProvider time,
        FakeResolver resolver,
        ConcurrentQueue<TimeSpan> delays,
        TeamId team) {
        Jira = jira;
        DataSource = dataSource;
        Keyring = keyring;
        Credentials = credentials;
        Throttle = throttle;
        Cache = cache;
        Client = client;
        Time = time;
        Resolver = resolver;
        Delays = delays;
        Team = team;
    }

    public FakeJiraServer Jira { get; }

    public NpgsqlDataSource DataSource { get; }

    public Keyring Keyring { get; }

    public Credentials Credentials { get; }

    public JiraThrottle Throttle { get; }

    public JiraCache Cache { get; }

    public JiraClient Client { get; }

    public FakeTimeProvider Time { get; }

    public FakeResolver Resolver { get; }

    public ConcurrentQueue<TimeSpan> Delays { get; }

    public TeamId Team { get; }

    public static string NewKeys(params string[] ids) =>
        string.Join(',', ids.Select(id => $"{id}:{Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))}"));

    public static async Task<JiraHarness> StartAsync(
        PostgresFixture postgres,
        Func<JiraClientOptions, JiraClientOptions>? configure = null,
        string? appKeys = null) {
        var database = await postgres.CreateDatabaseAsync();
        var dataSource = Db.CreateDataSource(database.AppConnectionString);
        var jira = await FakeJiraServer.StartAsync();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));
        var delays = new ConcurrentQueue<TimeSpan>();
        Task Delay(TimeSpan wait, CancellationToken token) {
            token.ThrowIfCancellationRequested();
            delays.Enqueue(wait);
            time.Advance(wait);
            return Task.CompletedTask;
        }

        var resolver = new FakeResolver();
        var options = new JiraClientOptions {
            Scheme = "http",
            Port = jira.Port,
            Resolver = resolver,
            IsAllowedAddress = AllowFakeOrPublic,
            Jitter = () => 0.5,
        };
        options = configure?.Invoke(options) ?? options;

        var keyring = Keyring.Parse(appKeys ?? NewKeys("k1"));
        var credentials = new Credentials(keyring, dataSource, time);
        var throttle = new JiraThrottle(new JiraThrottleOptions(), time, Delay);
        var cache = new JiraCache(time);
        var client = new JiraClient(
            credentials, throttle, cache, JiraHostPolicy.AtlassianCloudOnly, options, time, Delay, NullLogger<JiraClient>.Instance);

        var team = new TeamId(Guid.NewGuid());
        var harness = new JiraHarness(jira, dataSource, keyring, credentials, throttle, cache, client, time, resolver, delays, team);
        await harness.ConfigureTeamAsync(team, jira.Email, jira.Token);
        return harness;
    }

    public async Task ConfigureTeamAsync(TeamId team, string email, string token, string? pointsFieldId = "customfield_10016") {
        var sealedCredentials = Credentials.Seal(team, email, token);
        await using var command = DataSource.CreateCommand("""
            insert into teams (id, name) values ($1, 'Acme') on conflict do nothing;
            """);
        command.Parameters.Add(new NpgsqlParameter { Value = team.Value });
        await command.ExecuteNonQueryAsync();

        await using var config = DataSource.CreateCommand("""
            insert into team_jira_config (team_id, site_host, credentials_box, key_id, token_last4, email_masked,
                project_key, board_id, points_field_id, sprint_field_id, status)
            values ($1, $2, $3, $4, $5, $6, 'DUN', 7, $7, 'customfield_10020', 'ok')
            on conflict (team_id) do update set credentials_box = excluded.credentials_box, key_id = excluded.key_id,
                points_field_id = excluded.points_field_id
            """);
        config.Parameters.Add(new NpgsqlParameter { Value = team.Value });
        config.Parameters.Add(new NpgsqlParameter { Value = SiteHost });
        config.Parameters.Add(new NpgsqlParameter { Value = sealedCredentials.Box });
        config.Parameters.Add(new NpgsqlParameter { Value = sealedCredentials.KeyId });
        config.Parameters.Add(new NpgsqlParameter { Value = sealedCredentials.TokenLast4 });
        config.Parameters.Add(new NpgsqlParameter { Value = sealedCredentials.EmailMasked });
        config.Parameters.Add(new NpgsqlParameter { Value = (object?)pointsFieldId ?? DBNull.Value });
        await config.ExecuteNonQueryAsync();
        Credentials.Invalidate(team);
    }

    public async Task<string> StatusAsync(TeamId team) {
        await using var command = DataSource.CreateCommand("select status from team_jira_config where team_id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = team.Value });
        return (string)(await command.ExecuteScalarAsync())!;
    }

    public async Task WaitForRequestsAsync(int count) {
        for (var i = 0; i < 500 && Jira.Requests.Count < count; i++) {
            await Task.Delay(10);
        }
        Assert.True(Jira.Requests.Count >= count, $"expected {count} requests, saw {Jira.Requests.Count}");
    }

    public async ValueTask DisposeAsync() {
        Client.Dispose();
        Throttle.Dispose();
        await Jira.DisposeAsync();
        await DataSource.DisposeAsync();
    }
}
