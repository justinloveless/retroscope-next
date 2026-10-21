using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Retroscope.Fakes.FakeOidc;

/// <summary>One authorization or token request the fake received, with its query or form values.</summary>
public sealed record FakeOidcRequest(string Method, string Path, IReadOnlyDictionary<string, string> Values);

/// <summary>
/// An in-process stand-in for Google and Microsoft Entra on plain HTTP over loopback: the authorization endpoint
/// (answers at once with a redirect carrying the code, as if the user had consented), the token endpoint (checks the
/// client secret, the redirect URI and the PKCE verifier; a code works once) and the profile-picture host. ID tokens
/// carry a junk signature, because the server under test never checks it (§e).
/// </summary>
/// <remarks>
/// Google lives under <c>/google/{authorize,token}</c>, Entra under <c>/entra/{tenant}/oauth2/v2.0/{authorize,token}</c>
/// (so <see cref="EntraAuthority"/> plays <c>https://login.microsoftonline.com</c>), pictures under <c>/picture/…</c>.
/// </remarks>
public sealed class FakeOidcServer : IAsyncDisposable {
    // A 1×1 PNG with IHDR, IDAT and IEND, so the server's image sniffer accepts it.
    public static readonly byte[] DefaultPicture = Convert.FromBase64String(
        "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");

    private readonly WebApplication _app;
    private readonly ConcurrentQueue<FakeOidcRequest> _requests = new();
    private readonly ConcurrentDictionary<string, Grant> _grants = new(StringComparer.Ordinal);

    private FakeOidcServer(WebApplication app) {
        _app = app;
    }

    public int Port { get; private set; }

    public string BaseUrl => $"http://127.0.0.1:{Port}";

    public string GoogleAuthorizeUrl => $"{BaseUrl}/google/authorize";

    public string GoogleTokenUrl => $"{BaseUrl}/google/token";

    public string EntraAuthority => $"{BaseUrl}/entra";

    /// <summary>Registered clients: client id → secret.</summary>
    public ConcurrentDictionary<string, string> Clients { get; } = new(StringComparer.Ordinal);

    /// <summary>
    /// The user the next authorization signs in. <c>iss</c>, <c>aud</c>, <c>iat</c>, <c>exp</c> and <c>nonce</c> are
    /// filled in when absent (Entra's issuer from <c>tid</c>); a test sets any of them to tamper with the token.
    /// </summary>
    public JsonObject Claims { get; set; } = [];

    /// <summary>Answers the token endpoint with this status and an <c>invalid_grant</c> body instead, when set.</summary>
    public int? TokenStatus { get; set; }

    public byte[] Picture { get; set; } = DefaultPicture;

    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.UtcNow;

    public IReadOnlyList<FakeOidcRequest> Requests => [.. _requests];

    public static async Task<FakeOidcServer> StartAsync() {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));
        var app = builder.Build();
        var server = new FakeOidcServer(app);
        app.Run(server.HandleAsync);
        await app.StartAsync();
        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        server.Port = new Uri(address).Port;
        return server;
    }

    public async ValueTask DisposeAsync() => await _app.DisposeAsync();

    /// <summary>Mints an ID token for <paramref name="claims"/> as this fake does (header, payload, junk signature).</summary>
    public static string Mint(JsonObject claims) {
        var header = Base64Url.EncodeToString("""{"alg":"RS256","kid":"fake","typ":"JWT"}"""u8);
        var payload = Base64Url.EncodeToString(Encoding.UTF8.GetBytes(claims.ToJsonString()));
        return $"{header}.{payload}.{Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(256))}";
    }

    private async Task HandleAsync(HttpContext context) {
        var request = context.Request;
        var path = request.Path.Value ?? "";
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        Dictionary<string, string> values;
        if (request.HasFormContentType) {
            var form = await request.ReadFormAsync();
            values = form.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.Ordinal);
        } else {
            values = request.Query.ToDictionary(x => x.Key, x => x.Value.ToString(), StringComparer.Ordinal);
        }
        _requests.Enqueue(new FakeOidcRequest(request.Method, path, values));

        switch (segments) {
            case ["picture", ..] when HttpMethods.IsGet(request.Method):
                context.Response.ContentType = "image/png";
                await context.Response.Body.WriteAsync(Picture);
                return;
            case ["google", "authorize"] when HttpMethods.IsGet(request.Method):
                Authorize(context, "google", tenant: null, values);
                return;
            case ["entra", var tenant, "oauth2", "v2.0", "authorize"] when HttpMethods.IsGet(request.Method):
                Authorize(context, "entra", tenant, values);
                return;
            case ["google", "token"] when HttpMethods.IsPost(request.Method):
                await TokenAsync(context, "google", values);
                return;
            case ["entra", _, "oauth2", "v2.0", "token"] when HttpMethods.IsPost(request.Method):
                await TokenAsync(context, "entra", values);
                return;
            default:
                context.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
        }
    }

    private void Authorize(HttpContext context, string provider, string? tenant, Dictionary<string, string> query) {
        string? Value(string name) => query.GetValueOrDefault(name);
        var redirect = Value("redirect_uri");
        var valid = Value("response_type") == "code"
            && Value("client_id") is { } clientId && Clients.ContainsKey(clientId)
            && redirect is not null
            && Value("response_mode") is null or "query"
            && Value("code_challenge_method") == "S256"
            && !string.IsNullOrEmpty(Value("code_challenge"))
            && !string.IsNullOrEmpty(Value("state"))
            && !string.IsNullOrEmpty(Value("nonce"));
        if (!valid) {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }

        var code = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));
        _grants[code] = new Grant(provider, tenant, Value("client_id")!, redirect!, Value("code_challenge")!, Value("nonce")!, (JsonObject)Claims.DeepClone());
        var separator = redirect!.Contains('?', StringComparison.Ordinal) ? '&' : '?';
        context.Response.Redirect($"{redirect}{separator}code={Uri.EscapeDataString(code)}&state={Uri.EscapeDataString(Value("state")!)}");
    }

    private async Task TokenAsync(HttpContext context, string provider, Dictionary<string, string> form) {
        string? Value(string name) => form.GetValueOrDefault(name);
        if (TokenStatus is { } status) {
            await WriteJsonAsync(context, status, new JsonObject { ["error"] = "invalid_grant" });
            return;
        }
        var verifier = Value("code_verifier") ?? "";
        var grant = Value("code") is { } code && _grants.TryRemove(code, out var granted) ? granted : null;
        var valid = Value("grant_type") == "authorization_code"
            && grant is not null
            && grant.Provider == provider
            && grant.ClientId == Value("client_id")
            && Clients.TryGetValue(grant.ClientId, out var secret) && secret == Value("client_secret")
            && grant.RedirectUri == Value("redirect_uri")
            && Base64Url.EncodeToString(SHA256.HashData(Encoding.ASCII.GetBytes(verifier))) == grant.Challenge;
        if (!valid) {
            await WriteJsonAsync(context, StatusCodes.Status400BadRequest, new JsonObject { ["error"] = "invalid_grant" });
            return;
        }

        var claims = grant!.Claims;
        var now = Now();
        claims.TryAdd("iss", provider == "google"
            ? "https://accounts.google.com"
            : $"https://login.microsoftonline.com/{claims["tid"]?.GetValue<string>()}/v2.0");
        claims.TryAdd("aud", grant.ClientId);
        claims.TryAdd("iat", now.ToUnixTimeSeconds());
        claims.TryAdd("exp", now.AddHours(1).ToUnixTimeSeconds());
        claims.TryAdd("nonce", grant.Nonce);
        await WriteJsonAsync(context, StatusCodes.Status200OK, new JsonObject {
            ["access_token"] = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24)),
            ["token_type"] = "Bearer",
            ["expires_in"] = 3599,
            ["scope"] = "openid email profile",
            ["id_token"] = Mint(claims),
        });
    }

    private static async Task WriteJsonAsync(HttpContext context, int status, JsonObject body) {
        context.Response.StatusCode = status;
        context.Response.ContentType = "application/json";
        await context.Response.WriteAsync(body.ToJsonString());
    }

    private sealed record Grant(
        string Provider,
        string? Tenant,
        string ClientId,
        string RedirectUri,
        string Challenge,
        string Nonce,
        JsonObject Claims);
}
