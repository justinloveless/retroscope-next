using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Core.Security;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Auth;
using Retroscope.Server.Data;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Endpoints;
using Retroscope.Server.Hosting;
using Retroscope.Server.Json;
using Retroscope.Server.Realtime.Actors;
using Retroscope.Server.Realtime.Rooms;
using Retroscope.Server.Realtime.Transport;

namespace Retroscope.Integration.Tests.Admin;

/// <summary>
/// The admin routes and jobs with everything they reach for real (hosting, database, leader, actors, transport, the
/// three rooms, auth, blobs) on Kestrel loopback against a migrated test database. The clock is fake, so the jobs'
/// own schedule never fires and each test runs a pass when it wants one; revocations are also recorded.
/// </summary>
internal sealed class AdminHost : IAsyncDisposable {
    public const string PublicUrl = "https://admin.test";

    private AdminHost(WebApplication app, TestDatabase database, FakeTimeProvider time, Uri baseUri) {
        App = app;
        Database = database;
        Time = time;
        BaseUri = baseUri;
        Client = new HttpClient { BaseAddress = baseUri };
    }

    public WebApplication App { get; }

    public TestDatabase Database { get; }

    public FakeTimeProvider Time { get; }

    public Uri BaseUri { get; }

    public HttpClient Client { get; }

    public RecordingRevoker Revoker => Service<RecordingRevoker>();

    public ActorRegistry Registry => Service<ActorRegistry>();

    public static CancellationToken Token => TestContext.Current.CancellationToken;

    public T Service<T>() where T : notnull => App.Services.GetRequiredService<T>();

    public static async Task<AdminHost> StartAsync(PostgresFixture postgres) {
        var database = await postgres.CreateDatabaseAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["PUBLIC_URL"] = PublicUrl,
            ["WEB_ROOT"] = Path.Combine(Path.GetTempPath(), "rs-no-web-build"),
            ["DATABASE_URL"] = database.AppConnectionString,
            ["DATABASE_OWNER_URL"] = database.OwnerConnectionString,
            ["APP_KEYS"] = "k1:" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["ANON_TAG_KEY"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            [LeaderOptions.AcquireSecondsKey] = "20",
        });
        var services = builder.Services;
        services.ConfigureHttpJsonOptions(options => {
            var chain = options.SerializerOptions.TypeInfoResolverChain;
            chain.Insert(0, HostingJsonContext.Default);
            chain.Insert(1, AuthJsonContext.Default);
            chain.Insert(2, BlobJsonContext.Default);
            chain.Insert(3, AdminJsonContext.Default);
        });
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<RecordingRevoker>();
        services.AddSingleton<IConnectionRevoker>(sp => sp.GetRequiredService<RecordingRevoker>());
        HostingModule.AddServices(services, builder.Configuration);
        Db.AddServices(services, builder.Configuration);
        Leader.AddServices(services, builder.Configuration);
        ActorsModule.AddServices(services, builder.Configuration);
        TransportModule.AddServices(services, builder.Configuration);
        Server.Realtime.Rooms.PokerRoom.AddServices(services, builder.Configuration);
        RetroRoom.AddServices(services, builder.Configuration);
        Server.Realtime.Rooms.TeamRoom.AddServices(services, builder.Configuration);
        AuthEndpoints.AddServices(services, builder.Configuration);
        MeEndpoints.AddServices(services, builder.Configuration);
        BlobEndpoints.AddServices(services, builder.Configuration);
        AdminEndpoints.AddServices(services, builder.Configuration);

        var app = builder.Build();
        HostingModule.UsePipeline(app);
        HostingModule.Map(app);
        ActorsModule.Map(app);
        TransportModule.Map(app);
        AuthEndpoints.Map(app);
        MeEndpoints.Map(app);
        BlobEndpoints.Map(app);
        AdminEndpoints.Map(app);
        await app.StartAsync(Token);

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        return new AdminHost(app, database, time, new Uri(address));
    }

    /// <summary>A user straight in the database, with a live session; returns the user and the session cookie.</summary>
    public async Task<SignedIn> CreateUserAsync(string email, bool admin = false, string fullName = "Ada Lovelace") {
        var id = new PrincipalId(Guid.CreateVersion7());
        await ExecuteAsync("insert into principals (id, kind) values ($1, 'user')", id.Value);
        await ExecuteAsync(
            "insert into users (id, email, full_name, is_admin, email_verified_at) values ($1, $2, $3, $4, now())",
            id.Value,
            email,
            fullName,
            admin);
        var token = OpaqueToken.New();
        var now = Time.GetUtcNow();
        await Service<SessionsRepo>().InsertAsync(
            SessionsRepo.Insert(TokenHash.Of(token), id, AuthMethod.Password, now, now + Sessions.IdleLifetime, now + Sessions.AbsoluteLifetime, "test"),
            Token);
        return new SignedIn(id, token);
    }

    public async Task<HttpResponseMessage> SendAsync(SignedIn? who, HttpMethod method, string path, object? body = null) {
        using var request = new HttpRequestMessage(method, path);
        if (method != HttpMethod.Get) {
            request.Headers.Add("X-RS", "1");
            request.Headers.Add("Origin", PublicUrl);
        }
        if (who is not null) {
            request.Headers.Add("Cookie", $"{Sessions.CookieName}={who.Token}");
        }
        if (body is not null) {
            request.Content = JsonContent.Create(body);
        }
        return await Client.SendAsync(request, Token);
    }

    public Task<HttpResponseMessage> GetAsync(SignedIn? who, string path) => SendAsync(who, HttpMethod.Get, path);

    /// <summary>A socket authenticated by <paramref name="who"/>'s session cookie, past <c>hello</c>.</summary>
    public async Task<AdminSocket> ConnectAsync(SignedIn who) {
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        socket.Options.SetRequestHeader("Origin", PublicUrl);
        socket.Options.SetRequestHeader("Cookie", $"{Sessions.CookieName}={who.Token}");
        await socket.ConnectAsync(new Uri($"ws://{BaseUri.Authority}{WsEndpoint.Path}"), Token);
        var tab = new AdminSocket(socket);
        await tab.SendAsync(new HelloFrame(1, new ClientId(Guid.CreateVersion7()), []));
        await tab.NextAsync<WelcomeFrame>();
        return tab;
    }

    public async Task ExecuteAsync(string sql, params object[] parameters) {
        await using var connection = new NpgsqlConnection(Database.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters) {
            command.Parameters.Add(new() { Value = parameter });
        }
        await command.ExecuteNonQueryAsync(Token);
    }

    public async Task<T> ScalarAsync<T>(string sql, params object[] parameters) {
        await using var connection = new NpgsqlConnection(Database.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters) {
            command.Parameters.Add(new() { Value = parameter });
        }
        return (T)(await command.ExecuteScalarAsync(Token))!;
    }

    public async ValueTask DisposeAsync() {
        Client.Dispose();
        await App.StopAsync(CancellationToken.None);
        await App.DisposeAsync();
        await using var owner = new NpgsqlConnection(Database.OwnerConnectionString);
        NpgsqlConnection.ClearPool(owner);
    }
}

internal sealed record SignedIn(PrincipalId Id, string Token);

/// <summary>One tab's socket, read in order by the test itself.</summary>
internal sealed class AdminSocket(ClientWebSocket socket) : IDisposable {
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    public Task SendAsync(ClientFrame frame) =>
        socket.SendAsync(WireWriter.ToUtf8(frame), WebSocketMessageType.Text, endOfMessage: true, AdminHost.Token);

    public async Task<SnapFrame> SubscribeAsync(RoomKey room) {
        await SendAsync(new SubFrame(room));
        return await NextAsync<SnapFrame>();
    }

    /// <summary>Skips frames until one of type <typeparamref name="T"/> arrives.</summary>
    public async Task<T> NextAsync<T>() where T : ServerFrame {
        while (true) {
            if (await ReceiveAsync() is T frame) {
                return frame;
            }
        }
    }

    /// <summary>Reads until the server closes the socket; returns the close status.</summary>
    public async Task<int> WaitForCloseAsync() {
        while (true) {
            if (await ReceiveAsync() is null) {
                return (int)socket.CloseStatus!.Value;
            }
        }
    }

    public void Dispose() => socket.Dispose();

    /// <returns>The next frame, or null once the server closed the socket.</returns>
    private async Task<ServerFrame?> ReceiveAsync() {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(AdminHost.Token);
        timeout.CancelAfter(Timeout);
        var buffer = new byte[256 * 1024];
        var length = 0;
        WebSocketReceiveResult result;
        do {
            result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer, length, buffer.Length - length), timeout.Token);
            if (result.MessageType == WebSocketMessageType.Close) {
                return null;
            }
            length += result.Count;
        } while (!result.EndOfMessage);
        var parsed = WireParser.ParseServer(buffer.AsSpan(0, length));
        Assert.True(parsed.Ok, parsed.Message);
        return parsed.Frame;
    }
}

internal sealed class RecordingRevoker : IConnectionRevoker {
    public ConcurrentQueue<PrincipalId> Principals { get; } = new();

    public ConcurrentQueue<Guid> AuthSessions { get; } = new();

    public void RevokePrincipal(PrincipalId principal) => Principals.Enqueue(principal);

    public void RevokeAuthSession(Guid authSessionId) => AuthSessions.Enqueue(authSessionId);
}

internal static class AdminResponses {
    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(AdminHost.Token)).RootElement.Clone();

    public static async Task<string?> CodeAsync(this HttpResponseMessage response) =>
        (await response.JsonAsync()).TryGetProperty("code", out var code) ? code.GetString() : null;
}
