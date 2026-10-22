using System.Net;
using Microsoft.AspNetCore.Http;

namespace Retroscope.Fakes.Host;

/// <summary>
/// Relays a request verbatim (Host header included, redirects not followed) to a plain-HTTP fake, so the fake sees
/// exactly what Retroscope sent.
/// </summary>
public static class Relay {
    private static readonly HashSet<string> HopByHop = new(StringComparer.OrdinalIgnoreCase) {
        "Connection", "Keep-Alive", "Transfer-Encoding", "Upgrade", "Proxy-Connection", "TE", "Trailer", "Host",
    };

    public static HttpClient CreateClient(Uri upstream) => new(new SocketsHttpHandler {
        AllowAutoRedirect = false,
        UseCookies = false,
        UseProxy = false,
        AutomaticDecompression = DecompressionMethods.None,
    }) { BaseAddress = upstream };

    public static async Task SendAsync(HttpClient client, HttpContext context, string pathAndQuery) {
        var request = context.Request;
        using var message = new HttpRequestMessage(new HttpMethod(request.Method), pathAndQuery);
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

        using var response = await client.SendAsync(message, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted);
        context.Response.StatusCode = (int)response.StatusCode;
        foreach (var (name, values) in response.Headers.Concat(response.Content.Headers)) {
            if (!HopByHop.Contains(name)) {
                context.Response.Headers[name] = values.ToArray();
            }
        }
        await response.Content.CopyToAsync(context.Response.Body, context.RequestAborted);
    }
}
