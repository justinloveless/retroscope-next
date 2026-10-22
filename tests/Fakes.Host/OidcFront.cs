using System.Buffers.Text;
using System.Net;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Retroscope.Fakes.FakeOidc;

namespace Retroscope.Fakes.Host;

/// <summary>
/// Serves FakeOidc on a container port. An authorization request may carry <c>fake_claims</c> (base64url JSON): the
/// user that request signs in. Specs run in parallel and <see cref="FakeOidcServer.Claims"/> names only "the next
/// user", so setting the claims and relaying the authorization (which copies them into the grant) happen under one
/// lock.
/// </summary>
public sealed class OidcFront : IAsyncDisposable {
    private const string ClaimsParameter = "fake_claims";

    private readonly WebApplication _app;
    private readonly HttpClient _client;
    private readonly FakeOidcServer _oidc;
    private readonly SemaphoreSlim _authorize = new(1, 1);

    private OidcFront(WebApplication app, HttpClient client, FakeOidcServer oidc) {
        _app = app;
        _client = client;
        _oidc = oidc;
    }

    public static async Task<OidcFront> StartAsync(IPAddress address, int port, FakeOidcServer oidc) {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(address, port));
        var app = builder.Build();
        var front = new OidcFront(app, Relay.CreateClient(new Uri(oidc.BaseUrl)), oidc);
        app.Run(front.HandleAsync);
        await app.StartAsync();
        return front;
    }

    public async ValueTask DisposeAsync() {
        await _app.DisposeAsync();
        _client.Dispose();
        _authorize.Dispose();
    }

    private async Task HandleAsync(HttpContext context) {
        var request = context.Request;
        if (!request.Query.TryGetValue(ClaimsParameter, out var encoded)) {
            await Relay.SendAsync(_client, context, request.Path.Value + request.QueryString.Value);
            return;
        }

        JsonObject claims;
        try {
            claims = JsonNode.Parse(Base64Url.DecodeFromChars(encoded.ToString()))!.AsObject();
        } catch (Exception e) when (e is FormatException or System.Text.Json.JsonException or InvalidOperationException) {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            return;
        }
        var rest = QueryString.Create(request.Query.Where(x => x.Key != ClaimsParameter));
        await _authorize.WaitAsync(context.RequestAborted);
        try {
            _oidc.Claims = claims;
            await Relay.SendAsync(_client, context, request.Path.Value + rest.Value);
        } finally {
            _authorize.Release();
        }
    }
}
