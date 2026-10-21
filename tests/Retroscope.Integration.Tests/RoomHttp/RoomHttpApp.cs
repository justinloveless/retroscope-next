using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Room;
using Retroscope.Integration.Tests.Actors;
using Retroscope.Integration.Tests.Hosting;
using Retroscope.Integration.Tests.RetroRooms;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Auth;
using Retroscope.Server.Data;
using Retroscope.Server.Endpoints;
using Retroscope.Server.Hosting;
using Retroscope.Server.Realtime.Actors;
using Retroscope.Server.Realtime.Rooms;
using Retroscope.Server.Realtime.Transport;

namespace Retroscope.Integration.Tests.RoomHttp;

// The room endpoints on the real hosting pipeline with the real leader, actors and poker, retro and team rooms, against
// a migrated database. Callers come from TestApp's header ("user:<uuid>", "guest:<uuid>"); their principal rows are
// seeded as the owner. Time is frozen, so rate-limit buckets never refill during a test.
internal sealed class RoomHttpApp : IAsyncDisposable {
    public const string AnonTagKey = "AAECAwQFBgcICQoLDA0ODxAREhMUFRYXGBkaGxwdHh8=";

    private readonly TestApp _app;

    private RoomHttpApp(TestApp app, TestDatabase database, RecordingNotices notices) {
        _app = app;
        Database = database;
        Notices = notices;
    }

    public TestDatabase Database { get; }

    public RecordingNotices Notices { get; }

    public ActorRegistry Registry => _app.Services.GetRequiredService<ActorRegistry>();

    public static CancellationToken Token => TestContext.Current.CancellationToken;

    public static async Task<RoomHttpApp> StartAsync(PostgresFixture postgres) {
        var database = await postgres.CreateDatabaseAsync();
        var settings = new Dictionary<string, string?> {
            ["DATABASE_URL"] = database.AppConnectionString,
            ["DATABASE_OWNER_URL"] = database.OwnerConnectionString,
            [LeaderOptions.AcquireSecondsKey] = "20",
            ["ANON_TAG_KEY"] = AnonTagKey,
        };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var notices = new RecordingNotices();
        var app = await TestApp.StartAsync(
            settings,
            services => {
                services.AddSingleton<TimeProvider>(new FakeTimeProvider(DateTimeOffset.UtcNow));
                Db.AddServices(services, configuration);
                Leader.AddServices(services, configuration);
                ActorsModule.AddServices(services, configuration);
                TransportModule.AddServices(services, configuration);
                Server.Realtime.Rooms.PokerRoom.AddServices(services, configuration);
                RetroRoom.AddServices(services, configuration);
                Server.Realtime.Rooms.TeamRoom.AddServices(services, configuration);
                services.Replace(ServiceDescriptor.Singleton<ITeamNotices>(notices));
                services.AddSingleton(_ => new HashGate());
                PokerEndpoints.AddServices(services, configuration);
                RetroEndpoints.AddServices(services, configuration);
                MentionsEndpoints.AddServices(services, configuration);
            },
            app => {
                PokerEndpoints.Map(app);
                RetroEndpoints.Map(app);
                MentionsEndpoints.Map(app);
            });
        return new RoomHttpApp(app, database, notices);
    }

    public static string User(Guid id) => $"user:{id}";

    public static string Guest(Guid id) => $"guest:{id}";

    public static Viewer ViewerOf(string caller) {
        var parts = caller.Split(':');
        var kind = parts[0] == "guest" ? PrincipalKind.Guest : PrincipalKind.User;
        var principal = new Principal(new PrincipalId(Guid.Parse(parts[1])), kind, kind == PrincipalKind.Guest ? "Guest" : "Uma", null, false);
        return new Viewer(principal, new ClientId(Guid.NewGuid()), new ConnectionId(Guid.NewGuid()));
    }

    public async Task<HttpResponseMessage> SendAsync(
        HttpMethod method,
        string path,
        string? caller,
        object? body = null,
        string peer = "198.51.100.10") {
        using var request = new HttpRequestMessage(method, path);
        if (body is not null) {
            request.Content = new StringContent(body as string ?? JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }
        if (method != HttpMethod.Get) {
            request.Headers.Add(CsrfFilter.HeaderName, "1");
            request.Headers.Add("Origin", TestApp.PublicUrl);
        }
        request.Headers.Add(TestApp.PeerHeader, peer);
        if (caller is not null) {
            request.Headers.Add(TestApp.CallerHeader, caller);
        }
        return await _app.Client.SendAsync(request, Token);
    }

    public Task<HttpResponseMessage> GetAsync(string path, string? caller, string peer = "198.51.100.10") =>
        SendAsync(HttpMethod.Get, path, caller, null, peer);

    public Task<HttpResponseMessage> PostAsync(string path, string? caller, object? body = null, string peer = "198.51.100.10") =>
        SendAsync(HttpMethod.Post, path, caller, body ?? new { }, peer);

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response, HttpStatusCode expected = HttpStatusCode.OK) {
        var text = await response.Content.ReadAsStringAsync(Token);
        Assert.True(response.StatusCode == expected, $"{(int)response.StatusCode}: {text}");
        using var document = JsonDocument.Parse(text);
        return document.RootElement.Clone();
    }

    public static async Task<string> ProblemCodeAsync(HttpResponseMessage response, HttpStatusCode expected) {
        var body = await response.Content.ReadFromJsonAsync<JsonElement>(Token);
        Assert.Equal(expected, response.StatusCode);
        return body.GetProperty("code").GetString()!;
    }

    public async Task<Guid> UserAsync(string name = "Uma") {
        var id = Guid.CreateVersion7();
        await ExecAsync("insert into principals (id, kind) values ($1, 'user')", id);
        await ExecAsync("insert into users (id, email, full_name) values ($1, $2, $3)", id, $"{id:N}@example.com", name);
        return id;
    }

    public async Task<Guid> GuestAsync(string name = "Gus") {
        var id = Guid.CreateVersion7();
        await ExecAsync("insert into principals (id, kind, display_name) values ($1, 'guest', $2)", id, name);
        return id;
    }

    public async Task<Guid> TeamAsync(params (Guid User, string Role)[] members) {
        var id = Guid.CreateVersion7();
        await ExecAsync("insert into teams (id, name) values ($1, 'Team')", id);
        foreach (var (user, role) in members) {
            await ExecAsync("insert into team_members (team_id, user_id, role) values ($1, $2, $3)", id, user, role);
        }
        return id;
    }

    /// <summary>A peer subscribed through the registry, as the transport would subscribe a socket.</summary>
    public async Task<TestPeer> SubscribeAsync(RoomKey room, string caller) {
        var peer = new TestPeer(ViewerOf(caller));
        var posted = await Registry.SubscribeAsync(room, peer, peer.Viewer, null, null, Token);
        Assert.Equal(PostResult.Accepted, posted);
        return peer;
    }

    public async Task<T> ScalarAsync<T>(string sql, params object?[] values) {
        await using var connection = new NpgsqlConnection(Database.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = Command(connection, sql, values);
        return (T)(await command.ExecuteScalarAsync(Token))!;
    }

    public async Task ExecAsync(string sql, params object?[] values) {
        await using var connection = new NpgsqlConnection(Database.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = Command(connection, sql, values);
        await command.ExecuteNonQueryAsync(Token);
    }

    public ValueTask DisposeAsync() => _app.DisposeAsync();

    private static NpgsqlCommand Command(NpgsqlConnection connection, string sql, object?[] values) {
        var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values) {
            command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        }
        return command;
    }
}
