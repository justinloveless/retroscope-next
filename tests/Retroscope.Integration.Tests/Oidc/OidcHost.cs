using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Room;
using Retroscope.Core.Security;
using Retroscope.Fakes.FakeOidc;
using Retroscope.Integration.Tests.Auth;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Auth;
using Retroscope.Server.Data;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Endpoints;
using Retroscope.Server.Hosting;
using Retroscope.Server.Json;

namespace Retroscope.Integration.Tests.Oidc;

// Hosting, the database, auth, blobs and OIDC on a TestServer, against a fresh database and an in-process fake
// provider. Google and Entra are both configured; Entra allows TenantA (trusted domain acme.com) and TenantB.
internal sealed class OidcHost : IAsyncDisposable {
    public const string PublicUrl = AuthHost.PublicUrl;
    public const string GoogleClient = "google-client";
    public const string EntraClient = "entra-client";
    public const string TenantA = "6f0c3b9e-1d2a-4c55-9a7e-0b1e2d3c4f5a";
    public const string TenantB = "0a1b2c3d-4e5f-4a6b-8c7d-9e0f1a2b3c4d";
    public const string PictureUrl = "https://lh3.googleusercontent.com/a/ACg8ocK-fake=s96-c";

    private OidcHost(WebApplication app, TestDatabase database, FakeOidcServer provider) {
        App = app;
        Database = database;
        Provider = provider;
        Client = app.GetTestClient();
    }

    public WebApplication App { get; }

    public TestDatabase Database { get; }

    public FakeOidcServer Provider { get; }

    public HttpClient Client { get; }

    public RecordingRevoker Revoker => App.Services.GetRequiredService<RecordingRevoker>();

    public static CancellationToken Token => TestContext.Current.CancellationToken;

    public static async Task<OidcHost> StartAsync(PostgresFixture postgres, IDictionary<string, string?>? settings = null) {
        var provider = await FakeOidcServer.StartAsync();
        provider.Clients[GoogleClient] = "google-secret";
        provider.Clients[EntraClient] = "entra-secret";
        var database = await postgres.CreateDatabaseAsync();

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var configuration = new Dictionary<string, string?> {
            ["PUBLIC_URL"] = PublicUrl,
            ["WEB_ROOT"] = Path.Combine(Path.GetTempPath(), "rs-no-web-build"),
            ["DATABASE_URL"] = database.AppConnectionString,
            ["DATABASE_OWNER_URL"] = database.OwnerConnectionString,
            ["APP_KEYS"] = "k1:" + Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)),
            ["GOOGLE_CLIENT_ID"] = GoogleClient,
            ["GOOGLE_CLIENT_SECRET"] = "google-secret",
            ["GOOGLE_AUTHORIZE_URL"] = provider.GoogleAuthorizeUrl,
            ["GOOGLE_TOKEN_URL"] = provider.GoogleTokenUrl,
            ["ENTRA_CLIENT_ID"] = EntraClient,
            ["ENTRA_CLIENT_SECRET"] = "entra-secret",
            ["ENTRA_AUTHORITY_URL"] = provider.EntraAuthority,
            ["ENTRA_ALLOWED_TENANTS"] = $"{TenantA}, {TenantB.ToUpperInvariant()}",
            ["ENTRA_TENANT_DOMAINS"] = $"{TenantA}=acme.com|acme.io",
        };
        foreach (var (key, value) in settings ?? new Dictionary<string, string?>()) {
            configuration[key] = value;
        }
        builder.Configuration.AddInMemoryCollection(configuration);

        var services = builder.Services;
        services.ConfigureHttpJsonOptions(options => {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, HostingJsonContext.Default);
            options.SerializerOptions.TypeInfoResolverChain.Insert(1, AuthJsonContext.Default);
            options.SerializerOptions.TypeInfoResolverChain.Insert(2, OidcJsonContext.Default);
        });
        services.AddSingleton<RecordingRevoker>();
        services.AddSingleton<IConnectionRevoker>(sp => sp.GetRequiredService<RecordingRevoker>());
        services.AddSingleton(_ => OidcOptions.FromConfiguration(builder.Configuration) with {
            CreateHandler = () => new PictureHostRedirect(provider) { InnerHandler = new SocketsHttpHandler { AllowAutoRedirect = false } },
        });
        HostingModule.AddServices(services, builder.Configuration);
        Db.AddServices(services, builder.Configuration);
        AuthEndpoints.AddServices(services, builder.Configuration);
        MeEndpoints.AddServices(services, builder.Configuration);
        BlobEndpoints.AddServices(services, builder.Configuration);
        OidcEndpoints.AddServices(services, builder.Configuration);

        var app = builder.Build();
        HostingModule.UsePipeline(app);
        HostingModule.Map(app);
        AuthEndpoints.Map(app);
        MeEndpoints.Map(app);
        OidcEndpoints.Map(app);
        await app.StartAsync(Token);
        return new OidcHost(app, database, provider);
    }

    public OidcBrowser Browser() => new(this);

    public static JsonObject Google(string sub, string? email, bool? emailVerified = true, string name = "Grace Hopper") {
        var claims = new JsonObject { ["sub"] = sub, ["name"] = name };
        if (email is not null) {
            claims["email"] = email;
        }
        if (emailVerified is { } verified) {
            claims["email_verified"] = verified;
        }
        return claims;
    }

    public static JsonObject Entra(string tenant, string oid, string? email, bool? edov = null, string name = "Alan Turing") {
        var claims = new JsonObject {
            ["tid"] = tenant,
            ["oid"] = oid,
            ["sub"] = "pairwise-" + oid,
            ["name"] = name,
            ["preferred_username"] = email ?? "someone@" + tenant,
        };
        if (email is not null) {
            claims["email"] = email;
        }
        if (edov is { } value) {
            claims["xms_edov"] = value;
        }
        return claims;
    }

    public async Task<PrincipalId> CreateUserAsync(string email, bool verified, string? passwordHash = AuthHost.ImportedHash) {
        var id = new PrincipalId(Guid.CreateVersion7());
        await ExecuteAsync("insert into principals (id, kind) values ($1, 'user')", id.Value);
        await ExecuteAsync(
            "insert into users (id, email, password_hash, email_verified_at, full_name) values ($1, $2, $3, $4, 'Local Owner')",
            id.Value,
            email,
            (object?)passwordHash ?? DBNull.Value,
            verified ? DateTimeOffset.UtcNow : DBNull.Value);
        return id;
    }

    public async Task LinkAsync(PrincipalId user, string provider, string subject) =>
        await ExecuteAsync("insert into external_identities (provider, subject, user_id) values ($1, $2, $3)", provider, subject, user.Value);

    public async Task<string> CreateSessionAsync(PrincipalId user) {
        var token = OpaqueToken.New();
        var now = DateTimeOffset.UtcNow;
        await App.Services.GetRequiredService<SessionsRepo>().InsertAsync(
            SessionsRepo.Insert(TokenHash.Of(token), user, AuthMethod.Password, now, now + Sessions.IdleLifetime, now + Sessions.AbsoluteLifetime, "test"),
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

    public async Task<T?> ScalarAsync<T>(string sql, params object[] parameters) {
        await using var connection = new NpgsqlConnection(Database.OwnerConnectionString);
        await connection.OpenAsync(Token);
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (var parameter in parameters) {
            command.Parameters.Add(new() { Value = parameter });
        }
        var value = await command.ExecuteScalarAsync(Token);
        return value is null or DBNull ? default : (T)value;
    }

    public Task<Guid?> LinkedUserAsync(string provider, string subject) =>
        ScalarAsync<Guid?>("select user_id from external_identities where provider = $1 and subject = $2", provider, subject);

    public async ValueTask DisposeAsync() {
        Client.Dispose();
        await App.DisposeAsync();
        await Provider.DisposeAsync();
        await using var owner = new NpgsqlConnection(Database.OwnerConnectionString);
        NpgsqlConnection.ClearPool(owner);
    }

    // The picture host is the real Google one in the claims; this sends its requests to the fake instead.
    private sealed class PictureHostRedirect(FakeOidcServer provider) : DelegatingHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            if (request.RequestUri is { Host: OidcOptions.GooglePictureHost } uri) {
                request.RequestUri = new Uri($"{provider.BaseUrl}/picture{uri.PathAndQuery}");
            }
            return base.SendAsync(request, cancellationToken);
        }
    }
}

/// <summary>A browser against <see cref="OidcHost"/>: a cookie jar, CSRF headers on unsafe requests, no redirects followed.</summary>
internal sealed class OidcBrowser(OidcHost host) {
    private static readonly HttpClient ProviderClient = new(new SocketsHttpHandler { AllowAutoRedirect = false });

    private readonly Dictionary<string, string> _cookies = new(StringComparer.Ordinal);

    public string? Cookie(string name) => _cookies.GetValueOrDefault(name);

    public void SetCookie(string name, string value) => _cookies[name] = value;

    public void RemoveCookie(string name) => _cookies.Remove(name);

    public Task<HttpResponseMessage> GetAsync(string path) => SendAsync(HttpMethod.Get, path, null);

    public Task<HttpResponseMessage> PostAsync(string path, HttpContent? content = null) => SendAsync(HttpMethod.Post, path, content);

    public async Task<HttpResponseMessage> SendAsync(HttpMethod method, string path, HttpContent? content) {
        using var request = new HttpRequestMessage(method, path) { Content = content };
        if (method != HttpMethod.Get) {
            request.Headers.Add("X-RS", "1");
            request.Headers.Add("Origin", OidcHost.PublicUrl);
        }
        if (_cookies.Count > 0) {
            request.Headers.Add("Cookie", string.Join("; ", _cookies.Select(x => $"{x.Key}={x.Value}")));
        }
        var response = await host.Client.SendAsync(request, OidcHost.Token);
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

    /// <summary>Starts sign-in and lets the fake provider authorize; returns the callback path the provider redirected to.</summary>
    public async Task<string> AuthorizeAsync(string provider, string? returnPath = null) {
        var start = await GetAsync($"/auth/oidc/{provider}/start" + (returnPath is null ? "" : "?return=" + Uri.EscapeDataString(returnPath)));
        Assert.Equal(HttpStatusCode.Redirect, start.StatusCode);
        return await FollowProviderAsync(start.Headers.Location!);
    }

    public static async Task<string> FollowProviderAsync(Uri authorizeUrl) {
        using var authorized = await ProviderClient.GetAsync(authorizeUrl, OidcHost.Token);
        Assert.Equal(HttpStatusCode.Redirect, authorized.StatusCode);
        var callback = authorized.Headers.Location!;
        Assert.StartsWith(OidcHost.PublicUrl + "/auth/oidc/", callback.AbsoluteUri, StringComparison.Ordinal);
        return callback.PathAndQuery;
    }

    /// <summary>The whole round trip; returns the final redirect of the callback.</summary>
    public async Task<HttpResponseMessage> SignInAsync(string provider, JsonObject claims, string? returnPath = null) {
        host.Provider.Claims = claims;
        return await GetAsync(await AuthorizeAsync(provider, returnPath));
    }
}

internal static class OidcAssert {
    public static void RedirectsTo(HttpResponseMessage response, string location) {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(location, response.Headers.Location!.OriginalString);
    }
}
