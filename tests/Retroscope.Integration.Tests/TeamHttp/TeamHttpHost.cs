using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Room;
using Retroscope.Core.Security;
using Retroscope.Integration.Tests.Actors;
using Retroscope.Integration.Tests.Hosting;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Auth;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Hosting;
using Retroscope.Server.Realtime.Actors;

namespace Retroscope.Integration.Tests.TeamHttp;

/// <summary>A signed-in user of the test host; <see cref="Token"/> is null for an anonymous visitor.</summary>
internal sealed record TestUser(PrincipalId Id, string Email, string Name, string? Token) {
    public Guid Guid => Id.Value;

    public Viewer Viewer(int connection = 0) => new(
        new Principal(Id, PrincipalKind.User, Name, null, false),
        new ClientId(Guid.CreateVersion7()),
        new ConnectionId(new Guid($"00000000-0000-4000-9000-{connection:x12}")));
}

/// <summary>
/// The whole server as <c>Program.cs</c> composes it (real leader, actors, team room, email outbox) on a fresh database.
/// Users and sessions are written straight to the database; requests carry the session cookie and same-origin CSRF
/// headers, as a browser would.
/// </summary>
internal sealed class TeamHttpHost : IAsyncDisposable {
    public static readonly TestUser Anonymous = new(default, "", "", null);

    private readonly WebApplicationFactory<Program> _factory;
    private int _users;

    private TeamHttpHost(WebApplicationFactory<Program> factory, TestDatabase database) {
        _factory = factory;
        Database = database;
        Client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false, HandleCookies = false });
    }

    public TestDatabase Database { get; }

    public HttpClient Client { get; }

    public ActorRegistry Registry => Service<ActorRegistry>();

    public static CancellationToken Token => TestContext.Current.CancellationToken;

    public T Service<T>() where T : notnull => _factory.Services.GetRequiredService<T>();

    public static async Task<TeamHttpHost> StartAsync(PostgresFixture postgres) => Start(await postgres.CreateDatabaseAsync());

    /// <summary>A host on an existing database, as a restarted server finds it.</summary>
    public static TeamHttpHost Start(TestDatabase database) {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => {
            builder.UseSetting("PUBLIC_URL", TestApp.PublicUrl);
            builder.UseSetting("DATABASE_URL", database.AppConnectionString);
            builder.UseSetting("DATABASE_OWNER_URL", database.OwnerConnectionString);
            builder.UseSetting("WEB_ROOT", Path.Combine(Path.GetTempPath(), "rs-no-web-build"));
            builder.UseSetting("APP_KEYS", "k1:" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting("ANON_TAG_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        });
        return new TeamHttpHost(factory, database);
    }

    /// <summary>A user with a live password session.</summary>
    public async Task<TestUser> UserAsync(string name, bool verified = true, string? email = null) {
        var n = Interlocked.Increment(ref _users);
        email ??= $"{name.ToLowerInvariant()}{n}@example.com";
        var id = new PrincipalId(Guid.CreateVersion7());
        await ExecuteAsync("insert into principals (id, kind) values ($1, 'user')", id.Value);
        await ExecuteAsync(
            "insert into users (id, email, email_verified_at, full_name) values ($1, $2, $3, $4)",
            id.Value,
            email,
            verified ? DateTimeOffset.UtcNow : null,
            name);
        var token = OpaqueToken.New();
        var now = DateTimeOffset.UtcNow;
        await Service<SessionsRepo>().InsertAsync(
            SessionsRepo.Insert(TokenHash.Of(token), id, AuthMethod.Password, now, now + Sessions.IdleLifetime, now + Sessions.AbsoluteLifetime, "test"),
            Token);
        return new TestUser(id, email, name, token);
    }

    /// <summary>A team created through the API by <paramref name="owner"/>, with the others added as rows (as the team actor writes them).</summary>
    public async Task<Guid> TeamAsync(TestUser owner, string name = "Platform", params (TestUser User, string Role)[] members) {
        using var response = await SendAsync(owner, HttpMethod.Post, "/api/teams", new { name });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var team = (await JsonAsync(response)).GetProperty("id").GetGuid();
        foreach (var (user, role) in members) {
            await ExecuteAsync("insert into team_members (team_id, user_id, role) values ($1, $2, $3)", team, user.Guid, role);
        }
        // Rows written behind the cache's back: the next lookup reads them.
        Service<MembershipCache>().Forget(new TeamId(team));
        return team;
    }

    public Task<HttpResponseMessage> GetAsync(TestUser user, string path) => SendAsync(user, HttpMethod.Get, path, null);

    public Task<HttpResponseMessage> PostAsync(TestUser user, string path, object? body = null) => SendAsync(user, HttpMethod.Post, path, body ?? new { });

    public async Task<HttpResponseMessage> SendAsync(TestUser user, HttpMethod method, string path, object? body) {
        using var request = new HttpRequestMessage(method, path);
        if (method != HttpMethod.Get) {
            request.Headers.Add(CsrfFilter.HeaderName, "1");
            request.Headers.Add("Origin", TestApp.PublicUrl);
        }
        if (user.Token is { } token) {
            request.Headers.Add("Cookie", $"{Sessions.CookieName}={token}");
        }
        if (body is not null) {
            request.Content = JsonContent.Create(body);
        }
        return await Client.SendAsync(request, Token);
    }

    public static async Task<JsonElement> JsonAsync(HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(Token)).RootElement.Clone();

    public static async Task<string?> CodeAsync(HttpResponseMessage response) {
        var json = await JsonAsync(response);
        return json.ValueKind == JsonValueKind.Object && json.TryGetProperty("code", out var code) ? code.GetString() : null;
    }

    /// <summary>Subscribes a test connection of <paramref name="user"/> to a room and waits for its snapshot.</summary>
    public async Task<TestPeer> SubscribeAsync(TestUser user, RoomKey room, int connection = 0) {
        var peer = new TestPeer(user.Viewer(connection));
        Assert.Equal(PostResult.Accepted, await Registry.SubscribeAsync(room, peer, peer.Viewer, null, null, Token));
        return peer;
    }

    public async Task ExecuteAsync(string sql, params object?[] values) {
        await using var connection = new NpgsqlConnection(Database.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values) {
            command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        }
        await command.ExecuteNonQueryAsync(Token);
    }

    public async Task<T> ScalarAsync<T>(string sql, params object?[] values) {
        await using var connection = new NpgsqlConnection(Database.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var value in values) {
            command.Parameters.Add(new NpgsqlParameter { Value = value ?? DBNull.Value });
        }
        return (T)(await command.ExecuteScalarAsync(Token))!;
    }

    public async ValueTask DisposeAsync() {
        Client.Dispose();
        await _factory.DisposeAsync();
        await using var owner = new NpgsqlConnection(Database.OwnerConnectionString);
        NpgsqlConnection.ClearPool(owner);
    }
}
