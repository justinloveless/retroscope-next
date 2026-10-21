using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Room;
using Retroscope.Integration.Tests.PokerRoom;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Realtime.Transport;

namespace Retroscope.Integration.Tests.Transport;

/// <summary>
/// The real server (<see cref="Program"/>, every module, real cookie auth and the real membership cache) on Kestrel
/// loopback against a migrated test database. People sign up and sign in over HTTP and reach rooms over real sockets.
/// Rows whose HTTP routes belong to other units (teams, sessions, boards) are written as the owner.
/// </summary>
internal sealed class RealServer : IAsyncDisposable {
    public const string PublicUrl = "https://realtime.test";
    public const string SessionCookie = "__Host-rs_sid";

    private readonly WebApplicationFactory<Program> _factory;

    private RealServer(WebApplicationFactory<Program> factory, TestDatabase database, Uri http, Uri socket) {
        _factory = factory;
        Database = database;
        Http = new HttpClient(new HttpClientHandler { UseCookies = false }) { BaseAddress = http };
        SocketUri = socket;
    }

    public TestDatabase Database { get; }

    public HttpClient Http { get; }

    public Uri SocketUri { get; }

    public IServiceProvider Services => _factory.Services;

    public static CancellationToken Token => TestContext.Current.CancellationToken;

    public static async Task<RealServer> StartAsync(PostgresFixture postgres) {
        var database = await postgres.CreateDatabaseAsync();
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => {
            builder.UseSetting("PUBLIC_URL", PublicUrl);
            builder.UseSetting("DATABASE_URL", database.AppConnectionString);
            builder.UseSetting("DATABASE_OWNER_URL", database.OwnerConnectionString);
            builder.UseSetting("WEB_ROOT", Path.Combine(Path.GetTempPath(), "rs-no-web-build"));
            builder.UseSetting("APP_KEYS", "k1:" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
            builder.UseSetting("ANON_TAG_KEY", Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)));
        });
        factory.UseKestrel(0);
        factory.StartServer();
        var address = new Uri(factory.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First());
        var http = new Uri($"http://127.0.0.1:{address.Port}");
        await WaitHealthyAsync(http);
        return new RealServer(factory, database, http, new Uri($"ws://127.0.0.1:{address.Port}{WsEndpoint.Path}"));
    }

    /// <summary>Signs a new user up and in through the real routes; returns who they are and their session cookie.</summary>
    public async Task<Member> SignUpAsync(string name) {
        var email = $"{Guid.NewGuid():N}@example.test";
        const string Password = "correct horse battery";
        using (var signup = await PostAsync("/api/auth/signup", new { email, password = Password, fullName = name })) {
            Assert.Equal(HttpStatusCode.Accepted, signup.StatusCode);
        }
        using var login = await PostAsync("/api/auth/login", new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var cookie = login.Headers.GetValues("Set-Cookie")
            .Select(x => x.Split(';')[0])
            .Single(x => x.StartsWith(SessionCookie + "=", StringComparison.Ordinal));
        var id = await ScalarAsync<Guid>("select id from users where email = $1", email);
        return new Member(new Principal(new PrincipalId(id), PrincipalKind.User, name, null, false), cookie);
    }

    /// <summary>A browser tab of <paramref name="member"/>: the session cookie and the page's Origin on the upgrade.</summary>
    public Task<PokerSocket> ConnectAsync(Member member) {
        return PokerSocket.ConnectAsync(
            SocketUri,
            new Dictionary<string, string> { ["Cookie"] = member.Cookie, ["Origin"] = PublicUrl },
            member.Who);
    }

    public async Task<TeamId> TeamAsync(params (Member Who, TeamRole Role)[] members) {
        var team = new TeamId(Guid.CreateVersion7());
        await ExecAsync("insert into teams (id, name) values ($1, 'Realtime')", team.Value);
        foreach (var (who, role) in members) {
            await ExecAsync(
                "insert into team_members (team_id, user_id, role) values ($1, $2, $3)",
                team.Value,
                who.Who.Id.Value,
                role.ToString().ToLowerInvariant());
        }
        return team;
    }

    public async Task ExecAsync(string sql, params object[] parameters) {
        await using var connection = new NpgsqlConnection(Database.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters) {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }
        await command.ExecuteNonQueryAsync(Token);
    }

    public async Task<T> ScalarAsync<T>(string sql, params object[] parameters) {
        await using var connection = new NpgsqlConnection(Database.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters) {
            command.Parameters.Add(new NpgsqlParameter { Value = parameter });
        }
        return (T)(await command.ExecuteScalarAsync(Token))!;
    }

    public async ValueTask DisposeAsync() {
        Http.Dispose();
        await _factory.DisposeAsync();
        await using var owner = new NpgsqlConnection(Database.OwnerConnectionString);
        NpgsqlConnection.ClearPool(owner);
    }

    private Task<HttpResponseMessage> PostAsync(string path, object body) {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = JsonContent.Create(body) };
        request.Headers.Add("X-RS", "1");
        request.Headers.Add("Origin", PublicUrl);
        return Http.SendAsync(request, Token);
    }

    // The leader takes the lease and migrates in the background; /healthz turns 200 once it may serve.
    private static async Task WaitHealthyAsync(Uri http) {
        using var client = new HttpClient { BaseAddress = http };
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        while (true) {
            try {
                using var response = await client.GetAsync("/healthz", Token);
                if (response.IsSuccessStatusCode) {
                    return;
                }
            } catch (HttpRequestException) when (DateTime.UtcNow < deadline) {
            }
            if (DateTime.UtcNow > deadline) {
                throw new TimeoutException("The server did not become healthy.");
            }
            await Task.Delay(50, Token);
        }
    }
}

internal sealed record Member(Principal Who, string Cookie);
