using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Retroscope.Fakes.Host;

/// <summary>
/// Terminates TLS and relays each request verbatim (Host header included, redirects not followed) to a plain-HTTP
/// fake, so the fake sees exactly what Retroscope's Jira client sent.
/// </summary>
public sealed class TlsFront : IAsyncDisposable {
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase) {
        "Connection", "Keep-Alive", "Transfer-Encoding", "Upgrade", "Proxy-Connection", "TE", "Trailer", "Host",
    };

    private readonly WebApplication _app;
    private readonly HttpClient _client;

    private TlsFront(WebApplication app, HttpClient client) {
        _app = app;
        _client = client;
    }

    public static async Task<TlsFront> StartAsync(IPAddress address, int port, X509Certificate2 certificate, Uri upstream) {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(address, port, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        var client = new HttpClient(new SocketsHttpHandler {
            AllowAutoRedirect = false,
            UseCookies = false,
            UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
        }) { BaseAddress = upstream };
        var front = new TlsFront(app, client);
        app.Run(front.RelayAsync);
        await app.StartAsync();
        return front;
    }

    public async ValueTask DisposeAsync() {
        await _app.DisposeAsync();
        _client.Dispose();
    }

    private async Task RelayAsync(HttpContext context) {
        var request = context.Request;
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), request.Path.Value + request.QueryString.Value);
        if (request.ContentLength > 0 || request.Headers.TransferEncoding.Count > 0) {
            message.Content = new StreamContent(request.Body);
        }
        foreach (var (name, values) in request.Headers) {
            if (HopByHop.Contains(name)) {
                continue;
            }
            if (!message.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values)) {
                message.Content?.Headers.TryAddWithoutValidation(name, (IEnumerable<string?>)values);
            }
        }
        message.Headers.Host = request.Host.Value;

        using var response = await _client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers)) {
            if (!HopByHop.Contains(name)) {
                context.Response.Headers[name] = values.ToArray();
            }
        }
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
}
