using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Room;
using Retroscope.Core.Security;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Auth;
using Retroscope.Server.Data;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Endpoints;
using Retroscope.Server.Hosting;
using Retroscope.Server.Json;

namespace Retroscope.Integration.Tests.Auth;

// Hosting, the database and the three auth modules on a TestServer, against a fresh database. The clock is fake so
// expiry and backoff can be stepped; the connection revoker and the account mail are recorders.
internal sealed class AuthHost : IAsyncDisposable {
    public const string PublicUrl = "https://retro.test";
    public const string PeerHeader = "X-Test-Peer";
    public const string DefaultPeer = "203.0.113.9";

    // GoTrue (golang.org/x/crypto/bcrypt) hash of "abc123", as imported from Supabase (U3 fixture).
    public const string ImportedHash = "$2a$10$6/9Q0A0VBki9ZWITkyLsP.fikMCefVUCk.vIZ1cRDukpsPrUxNp8u";
    public const string ImportedPassword = "abc123";

    private AuthHost(WebApplication app, TestDatabase database, FakeTimeProvider time) {
        App = app;
        Database = database;
        Time = time;
        Client = app.GetTestClient();
    }

    public WebApplication App { get; }

    public TestDatabase Database { get; }

    public FakeTimeProvider Time { get; }

    public HttpClient Client { get; }

    public RecordingRevoker Revoker => App.Services.GetRequiredService<RecordingRevoker>();

    public RecordingMail Mail => App.Services.GetRequiredService<RecordingMail>();

    public T Service<T>() where T : notnull => App.Services.GetRequiredService<T>();

    public static CancellationToken Token => TestContext.Current.CancellationToken;

    public static async Task<AuthHost> StartAsync(PostgresFixture postgres) {
        var database = await postgres.CreateDatabaseAsync();
        var time = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["PUBLIC_URL"] = PublicUrl,
            ["WEB_ROOT"] = Path.Combine(Path.GetTempPath(), "rs-no-web-build"),
            ["DATABASE_URL"] = database.AppConnectionString,
            ["DATABASE_OWNER_URL"] = database.OwnerConnectionString,
            ["APP_KEYS"] = "k1:" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
        });
        var services = builder.Services;
        services.ConfigureHttpJsonOptions(options => {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, HostingJsonContext.Default);
            options.SerializerOptions.TypeInfoResolverChain.Insert(1, AuthJsonContext.Default);
        });
        services.AddSingleton<TimeProvider>(time);
        services.AddSingleton<RecordingRevoker>();
        services.AddSingleton<IConnectionRevoker>(sp => sp.GetRequiredService<RecordingRevoker>());
        services.AddSingleton<RecordingMail>();
        services.AddSingleton<IAccountMail>(sp => sp.GetRequiredService<RecordingMail>());
        services.AddSingleton<IStartupFilter, PeerFilter>();
        HostingModule.AddServices(services, builder.Configuration);
        Db.AddServices(services, builder.Configuration);
        AuthEndpoints.AddServices(services, builder.Configuration);
        MeEndpoints.AddServices(services, builder.Configuration);

        var app = builder.Build();
        HostingModule.UsePipeline(app);
        HostingModule.Map(app);
        AuthEndpoints.Map(app);
        MeEndpoints.Map(app);
        await app.StartAsync(Token);
        return new AuthHost(app, database, time);
    }

    public Browser Browser(string peer = DefaultPeer) => new(this, peer);

    /// <summary>Inserts a user straight into the database, as the importer or an earlier signup would have.</summary>
    public async Task<PrincipalId> CreateUserAsync(
        string email,
        string? passwordHash = ImportedHash,
        bool verified = false,
        string fullName = "Ada Lovelace") {
        var id = new PrincipalId(Guid.CreateVersion7());
        await ExecuteAsync("insert into principals (id, kind) values ($1, 'user')", id.Value);
        await ExecuteAsync(
            "insert into users (id, email, password_hash, email_verified_at, full_name) values ($1, $2, $3, $4, $5)",
            id.Value,
            email,
            (object?)passwordHash ?? DBNull.Value,
            verified ? DateTimeOffset.UtcNow : DBNull.Value,
            fullName);
        return id;
    }

    /// <summary>A session row for <paramref name="user"/> created at the fake clock's now; returns its cookie token.</summary>
    public async Task<string> CreateSessionAsync(PrincipalId user, AuthMethod method) {
        var token = OpaqueToken.New();
        var now = Time.GetUtcNow();
        await Service<SessionsRepo>().InsertAsync(
            SessionsRepo.Insert(
                TokenHash.Of(token), user, method, now, now + Sessions.IdleLifetime, now + Sessions.AbsoluteLifetime, "test"),
            Token);
        return token;
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

    /// <summary>Counts the commands Npgsql runs against this test's database while <paramref name="action"/> runs.</summary>
    public async Task<int> CountQueriesAsync(Func<Task> action) {
        var count = 0;
        using var listener = new ActivityListener {
            ShouldListenTo = source => source.Name == "Npgsql",
            Sample = (ref ActivityCreationOptions<ActivityContext> _) => ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activity => {
                if (activity.TagObjects.Any(x => Equals(x.Value, Database.Name))) {
                    Interlocked.Increment(ref count);
                }
            },
        };
        ActivitySource.AddActivityListener(listener);
        await action();
        return Volatile.Read(ref count);
    }

    public async ValueTask DisposeAsync() {
        Client.Dispose();
        await App.DisposeAsync();
        // Idle pooled connections to a finished test's database would otherwise hold server slots for minutes.
        await using var owner = new NpgsqlConnection(Database.OwnerConnectionString);
        NpgsqlConnection.ClearPool(owner);
    }

    // TestServer leaves RemoteIpAddress empty; this stands in for the TCP peer, ahead of the whole pipeline.
    private sealed class PeerFilter : IStartupFilter {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => {
            app.Use((context, nextMiddleware) => {
                context.Connection.RemoteIpAddress = IPAddress.Parse(
                    context.Request.Headers.TryGetValue(PeerHeader, out var peer) ? peer.ToString() : DefaultPeer);
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}

/// <summary>One browser: its own cookie jar, its own address, and same-origin CSRF headers on unsafe requests.</summary>
internal sealed class Browser(AuthHost host, string peer) {
    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

    public string Peer { get; set; } = peer;

    public string? Cookie(string name) => _cookies.GetValueOrDefault(name);

    public void SetCookie(string name, string value) => _cookies[name] = value;

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path, null);

    public Task<HttpResponseMessage> PostAsync(string path, object? body = null) => SendAsync(HttpMethod.Post, path, body);

    public Task<HttpResponseMessage> PutAsync(string path, object? body) => SendAsync(HttpMethod.Put, path, body);

    public Task<HttpResponseMessage> PatchAsync(string path, object? body) => SendAsync(HttpMethod.Patch, path, body);

    public Task<HttpResponseMessage> DeleteAsync(string path) => SendAsync(HttpMethod.Delete, path, null);

    public Task<HttpResponseMessage> LoginAsync(string email, string password) =>
        PostAsync("/api/auth/login", new { email, password });

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, object? body) {
        using var request = new HttpRequestMessage(method, path);
        request.Headers.Add(AuthHost.PeerHeader, Peer);
        if (method != HttpMethod.Get) {
            request.Headers.Add("X-RS", "1");
            request.Headers.Add("Origin", AuthHost.PublicUrl);
        }
        if (_cookies.Count > 0) {
            request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(x => $"{x.Key}={x.Value}")));
        }
        if (body is not null) {
            request.Content = JsonContent.Create(body);
        }
        var response = await host.Client.SendAsync(request, AuthHost.Token);
        if (response.Headers.TryGetValues("Set-Cookie", out var setCookies)) {
            foreach (var header in setCookies) {
                var cookie = SetCookieHeader.Parse(header);
                if (cookie.MaxAge == TimeSpan.Zero) {
                    _cookies.Remove(cookie.Name);
                } else {
                    _cookies[cookie.Name] = cookie.Value;
                }
            }
        }
        return response;
    }
}

internal sealed record SetCookieHeader(string Name, string Value, TimeSpan? MaxAge, IReadOnlySet<string> Attributes) {
    public static SetCookieHeader Parse(string header) {
        var parts = header.Split(';', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var nameValue = parts[0].Split('=', 2);
        TimeSpan? maxAge = null;
        var attributes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in parts.Skip(1)) {
            var pair = part.Split('=', 2);
            if (pair[0].Equals("max-age", StringComparison.OrdinalIgnoreCase)) {
                maxAge = TimeSpan.FromSeconds(long.Parse(pair[1], System.Globalization.CultureInfo.InvariantCulture));
            }
            attributes.Add(part.ToLowerInvariant());
        }
        return new SetCookieHeader(nameValue[0], nameValue[1], maxAge, attributes);
    }

    public static SetCookieHeader Find(HttpResponseMessage response, string name) =>
        response.Headers.GetValues("Set-Cookie").Select(Parse).Single(x => x.Name == name);
}

internal sealed class RecordingRevoker : IConnectionRevoker {
    public ConcurrentQueue<Guid> AuthSessions { get; } = new();

    public ConcurrentQueue<PrincipalId> Principals { get; } = new();

    public void RevokePrincipal(PrincipalId principal) => Principals.Enqueue(principal);

    public void RevokeAuthSession(Guid authSessionId) => AuthSessions.Enqueue(authSessionId);
}

internal sealed class RecordingMail : IAccountMail {
    public ConcurrentQueue<(string Kind, PrincipalId User, string Email)> Sent { get; } = new();

    public Task SignedUpAsync(PrincipalId user, string email, IPAddress? ip, CancellationToken cancellationToken) =>
        Record("signed_up", user, email);

    public Task AlreadyRegisteredAsync(PrincipalId user, string email, IPAddress? ip, CancellationToken cancellationToken) =>
        Record("already_registered", user, email);

    public Task LoginCappedAsync(PrincipalId user, string email, IPAddress? ip, CancellationToken cancellationToken) =>
        Record("login_capped", user, email);

    private Task Record(string kind, PrincipalId user, string email) {
        Sent.Enqueue((kind, user, email));
        return Task.CompletedTask;
    }
}

internal static class ResponseExtensions {
    public static async Task<JsonElement> JsonAsync(this HttpResponseMessage response) =>
        JsonDocument.Parse(await response.Content.ReadAsStringAsync(AuthHost.Token)).RootElement.Clone();

    public static async Task<string?> ProblemCodeAsync(this HttpResponseMessage response) =>
        (await response.JsonAsync()).TryGetProperty("code", out var code) ? code.GetString() : null;
}
