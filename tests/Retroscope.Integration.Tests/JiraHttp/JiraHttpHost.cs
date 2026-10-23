using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Room;
using Retroscope.Fakes.FakeJira;
using Retroscope.Integration.Tests.Jira.Client;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Data;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Endpoints;
using Retroscope.Server.Hosting;
using Retroscope.Server.Jira;
using Retroscope.Server.Json;
using Retroscope.Server.Realtime.Actors;
using Retroscope.Server.Realtime.Rooms;
using Retroscope.Server.Realtime.Transport;

namespace Retroscope.Integration.Tests.JiraHttp;

/// <summary>
/// The Jira endpoints with everything they reach in production — hosting, database, leader, actor runtime, transport,
/// poker room, the real membership cache and the real Jira client — on Kestrel loopback, talking to FakeJira. Time
/// is fake, so cache lifetimes and the meta loop's coalescing run on demand.
/// </summary>
internal sealed class JiraHttpHost : IAsyncDisposable {
    public const string Origin = "https://jira.test";
    public const string CallerHeader = "X-Test-Caller";
    public const string SiteHost = JiraHarness.SiteHost;
    public const string PointsField = "customfield_10016";
    public const string SprintField = "customfield_10020";

    private JiraHttpHost(WebApplication app, TestDatabase database, FakeJiraServer jira, FakeTimeProvider time, Uri http, ConcurrentQueue<string> logs) {
        Logs = logs;
        App = app;
        Database = database;
        Jira = jira;
        Time = time;
        Http = new HttpClient { BaseAddress = http };
        SocketUri = new Uri($"ws://{http.Authority}{WsEndpoint.Path}");
    }

    public WebApplication App { get; }

    /// <summary>Every log line the server wrote, at every level, with its exception if any.</summary>
    public ConcurrentQueue<string> Logs { get; }

    public TestDatabase Database { get; }

    public FakeJiraServer Jira { get; }

    public FakeTimeProvider Time { get; }

    public HttpClient Http { get; }

    public Uri SocketUri { get; }

    public ActorRegistry Registry => App.Services.GetRequiredService<ActorRegistry>();

    public static CancellationToken Token => TestContext.Current.CancellationToken;

    public static async Task<JiraHttpHost> StartAsync(PostgresFixture postgres) {
        var database = await postgres.CreateDatabaseAsync();
        var jira = await FakeJiraServer.StartAsync();
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 20, 12, 0, 0, TimeSpan.Zero));

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        var logs = new ConcurrentQueue<string>();
        builder.Logging.SetMinimumLevel(LogLevel.Trace);
        builder.Logging.AddProvider(new CapturingLoggerProvider(logs));
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["PUBLIC_URL"] = Origin,
            ["WEB_ROOT"] = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "rs-no-web-build"),
            ["DATABASE_URL"] = database.AppConnectionString,
            ["DATABASE_OWNER_URL"] = database.OwnerConnectionString,
            ["APP_KEYS"] = JiraHarness.NewKeys("k1"),
            [LeaderOptions.AcquireSecondsKey] = "20",
        });
        var services = builder.Services;
        var configuration = builder.Configuration;
        services.AddSingleton<TimeProvider>(time);
        services.ConfigureHttpJsonOptions(options => {
            var chain = options.SerializerOptions.TypeInfoResolverChain;
            chain.Insert(0, HostingJsonContext.Default);
            chain.Insert(1, JiraHttpJsonContext.Default);
            chain.Insert(2, JiraClientJsonContext.Default);
        });
        HostingModule.AddServices(services, configuration);
        Db.AddServices(services, configuration);
        Leader.AddServices(services, configuration);
        ActorsModule.AddServices(services, configuration);
        TransportModule.AddServices(services, configuration);
        Server.Realtime.Rooms.PokerRoom.AddServices(services, configuration);
        JiraClient.AddServices(services, configuration);
        JiraEndpoints.AddServices(services, configuration);

        // Fake time never refills token buckets, so the tests get large ones.
        services.Replace(ServiceDescriptor.Singleton(new TransportOptions { FrameBurst = 10_000 }));
        services.AddSingleton<MembershipCache>();
        services.Replace(ServiceDescriptor.Singleton<IAccessView>(sp => sp.GetRequiredService<MembershipCache>()));
        services.AddSingleton<IMembershipLookup>(sp => sp.GetRequiredService<MembershipCache>());
        services.AddSingleton<ICallerResolver, HeaderCaller>();
        services.Replace(ServiceDescriptor.Singleton(sp => new JiraThrottle(
            new JiraThrottleOptions { RequestsPerSecond = 10_000, Burst = 10_000 },
            time,
            (_, token) => Task.Delay(TimeSpan.FromMilliseconds(1), token))));
        services.Replace(ServiceDescriptor.Singleton(sp => new JiraClient(
            sp.GetRequiredService<Credentials>(),
            sp.GetRequiredService<JiraThrottle>(),
            sp.GetRequiredService<JiraCache>(),
            sp.GetRequiredService<Retroscope.Core.Security.JiraHostPolicy>(),
            new JiraClientOptions {
                Scheme = "http",
                Port = jira.Port,
                Resolver = new FakeResolver(),
                IsAllowedAddress = JiraHarness.AllowFakeOrPublic,
                Jitter = () => 0,
            },
            time,
            (_, token) => Task.Delay(TimeSpan.FromMilliseconds(1), token),
            sp.GetRequiredService<ILogger<JiraClient>>())));

        var app = builder.Build();
        HostingModule.UsePipeline(app);
        HostingModule.Map(app);
        ActorsModule.Map(app);
        TransportModule.Map(app);
        Server.Realtime.Rooms.PokerRoom.Map(app);
        JiraEndpoints.Map(app);
        await app.StartAsync(Token);

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new JiraHttpHost(app, database, jira, time, new Uri(address), logs);
    }

    // Seeding: rows other units' routes would have written, inserted as the owner.

    public async Task<Principal> UserAsync(string name) {
        var id = Guid.CreateVersion7();
        await ExecuteAsync("insert into principals (id, kind) values ($1, 'user')", id);
        await ExecuteAsync("insert into users (id, email, full_name) values ($1, $2, $3)", id, $"{id:N}@example.test", name);
        return new Principal(new PrincipalId(id), PrincipalKind.User, name, null, false);
    }

    public async Task<TeamId> TeamAsync(params (Principal Who, string Role)[] members) {
        var team = new TeamId(Guid.CreateVersion7());
        await ExecuteAsync("insert into teams (id, name) values ($1, 'Team')", team.Value);
        foreach (var (who, role) in members) {
            await ExecuteAsync("insert into team_members (team_id, user_id, role) values ($1, $2, $3)", team.Value, who.Id.Value, role);
        }
        return team;
    }

    /// <summary>A team configured against FakeJira: board 7 of project RS, the default points and sprint fields.</summary>
    public async Task ConfigureAsync(TeamId team, bool membersCanEdit = true, string status = "ok", string? pointsField = PointsField) {
        var sealedBox = App.Services.GetRequiredService<Credentials>().Seal(team, Jira.Email, Jira.Token);
        await ExecuteAsync(
            """
            insert into team_jira_config (team_id, site_host, credentials_box, key_id, token_last4, email_masked, project_key,
                board_id, ticket_prefix, points_field_id, sprint_field_id, status, members_can_edit)
            values ($1, $2, $3, $4, $5, $6, 'RS', 7, 'RS', $7, $8, $9, $10)
            """,
            team.Value, SiteHost, sealedBox.Box, sealedBox.KeyId, sealedBox.TokenLast4, sealedBox.EmailMasked,
            (object?)pointsField ?? DBNull.Value, SprintField, status, membersCanEdit);
        App.Services.GetRequiredService<Credentials>().Invalidate(team);
    }

    /// <summary>A team session whose rounds carry <paramref name="keys"/>; <paramref name="inactive"/> rounds are no longer active.</summary>
    public async Task<RoomKey> SessionAsync(Principal creator, TeamId team, string[] keys, string[]? inactive = null, string[]? pending = null) {
        var session = Guid.CreateVersion7();
        await ExecuteAsync(
            "insert into poker_sessions (id, code, team_id, created_by, next_round_number) values ($1, $2, $3, $4, $5)",
            session, $"S{session:N}"[..12], team.Value, creator.Id.Value, keys.Length + 1);
        Guid first = default;
        for (var i = 0; i < keys.Length; i++) {
            var round = Guid.CreateVersion7();
            first = i == 0 ? round : first;
            var isPending = pending?.Contains(keys[i]) == true;
            await ExecuteAsync(
                """
                insert into poker_rounds (id, session_id, number, sort_key, ticket_key, is_active, is_pending, pending_owner_id)
                values ($1, $2, $3, $4, $5, $6, $7, $8)
                """,
                round, session, i + 1, i, keys[i], inactive?.Contains(keys[i]) != true, isPending,
                isPending ? creator.Id.Value : DBNull.Value);
        }
        await ExecuteAsync("update poker_sessions set current_round_id = $2 where id = $1", session, first);
        return RoomKey.Poker(new SessionId(session));
    }

    public async Task<RoundId[]> RoundsAsync(RoomKey room) {
        var rows = await RowsAsync("select id from poker_rounds where session_id = $1 order by sort_key", room.Id);
        return [.. rows.Select(row => new RoundId((Guid)row[0]!))];
    }

    public async Task<List<object?[]>> RowsAsync(string sql, params object?[] parameters) {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters) {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter ?? DBNull.Value });
        }
        var rows = new List<object?[]>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token)) {
            var row = new object?[reader.FieldCount];
            for (var i = 0; i < row.Length; i++) {
                row[i] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }
        return rows;
    }

    public async Task ExecuteAsync(string sql, params object?[] parameters) {
        await using var connection = await OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters) {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter ?? DBNull.Value });
        }
        await command.ExecuteNonQueryAsync(Token);
    }

    /// <summary>The audit rows of the team, oldest first: <c>action target</c>.</summary>
    public async Task<List<string>> AuditAsync(TeamId team) {
        var rows = await RowsAsync("select action, target from audit_log where team_id = $1 order by id", team.Value);
        return [.. rows.Select(row => $"{row[0]} {row[1]}".TrimEnd())];
    }

    // FakeJira content.

    public void AddBoard() {
        if (Jira.Boards.Count == 0) {
            Jira.AddBoard(7, "RS");
        }
    }

    /// <summary>Jira requests made since <paramref name="from"/> (FakeJira's request log index).</summary>
    public IReadOnlyList<FakeJiraRequest> JiraRequestsSince(int from) => [.. Jira.Requests.Skip(from)];

    // HTTP.

    public Task<HttpResponseMessage> GetAsync(Principal who, string path) => SendAsync(HttpMethod.Get, who, path, null);

    /// <param name="expectContinue">Sends <c>Expect: 100-continue</c>, so a body the server refuses by its declared length
    /// is never streamed.</param>
    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, Principal who, string path, object? body, bool expectContinue = false) {
        using var request = new HttpRequestMessage(method, path);
        if (expectContinue) {
            request.Headers.ExpectContinue = true;
        }
        request.Headers.Add(CallerHeader, $"user:{who.Id}:{who.DisplayName}");
        request.Headers.Add(CsrfFilter.HeaderName, "1");
        request.Headers.Add("Origin", Origin);
        if (body is not null) {
            request.Content = new StringContent(body as string ?? JsonSerializer.Serialize(body, body.GetType()), Encoding.UTF8, "application/json");
        }
        return await Http.SendAsync(request, Token);
    }

    public async Task<JsonNode> JsonAsync(HttpMethod method, Principal who, string path, object? body = null, int expected = 200) {
        using var response = await SendAsync(method, who, path, body);
        var text = await response.Content.ReadAsStringAsync(Token);
        Assert.True((int)response.StatusCode == expected, $"{method} {path} answered {(int)response.StatusCode}: {text}");
        return text.Length == 0 ? new JsonObject() : JsonNode.Parse(text)!;
    }

    /// <summary>A problem response: its status and code.</summary>
    public async Task<(int Status, JsonNode Body)> ProblemAsync(HttpMethod method, Principal who, string path, object? body = null, bool expectContinue = false) {
        using var response = await SendAsync(method, who, path, body, expectContinue);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        return ((int)response.StatusCode, JsonNode.Parse(await response.Content.ReadAsStringAsync(Token))!);
    }

    public static string Url(TeamId team, string suffix = "") => $"/api/teams/{team.Value}/jira{suffix}";

    // Realtime.

    public async Task<JiraSocket> JoinAsync(Principal who, RoomKey room) {
        var socket = await JiraSocket.ConnectAsync(this, who);
        await socket.SubscribeAsync(room);
        return socket;
    }

    public Task<T> QueryAsync<T>(RoomKey room, Func<PokerState, T> read) =>
        Registry.QueryAsync<PokerState, PokerInput, T>(room, view => ValueTask.FromResult(read(view.State!)), Token);

    /// <summary>Advances fake time and lets the room handle whatever fired.</summary>
    public async Task AdvanceAsync(RoomKey room, TimeSpan by) {
        Time.Advance(by);
        await QueryAsync(room, _ => 0);
    }

    public async ValueTask DisposeAsync() {
        Http.Dispose();
        await App.StopAsync(CancellationToken.None);
        await App.DisposeAsync();
        await Jira.DisposeAsync();
    }

    private async Task<NpgsqlConnection> OpenAsync() {
        var builder = new NpgsqlConnectionStringBuilder(Database.OwnerConnectionString) { Pooling = false };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(Token);
        return connection;
    }

    private sealed class CapturingLoggerProvider(ConcurrentQueue<string> logs) : ILoggerProvider {
        public ILogger CreateLogger(string categoryName) => new CapturingLogger(categoryName, logs);

        public void Dispose() {
        }
    }

    private sealed class CapturingLogger(string category, ConcurrentQueue<string> logs) : ILogger {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            logs.Enqueue($"{logLevel} {category}: {formatter(state, exception)} {exception}");
    }

    // "user:<principal uuid>:<display name>"
    private sealed class HeaderCaller : ICallerResolver {
        public ValueTask<Caller?> ResolveAsync(HttpContext context) {
            if (!context.Request.Headers.TryGetValue(CallerHeader, out var header)) {
                return ValueTask.FromResult<Caller?>(null);
            }
            var parts = header.ToString().Split(':', 3);
            var principal = new Principal(new PrincipalId(Guid.Parse(parts[1])), PrincipalKind.User, parts[2], null, false);
            return ValueTask.FromResult<Caller?>(new Caller(principal, true));
        }
    }
}
