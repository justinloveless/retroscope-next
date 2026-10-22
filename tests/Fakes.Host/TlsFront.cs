using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Logging;

namespace Retroscope.Fakes.Host;

/// <summary>Terminates TLS in front of a plain-HTTP fake and relays each request to it (see <see cref="Relay"/>).</summary>
public sealed class TlsFront : IAsyncDisposable {
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
        var front = new TlsFront(app, Relay.CreateClient(upstream));
        app.Run(context => Relay.SendAsync(front._client, context, context.Request.Path.Value + context.Request.QueryString.Value));
        await app.StartAsync();
        return front;
    }

    public async ValueTask DisposeAsync() {
        await _app.DisposeAsync();
        _client.Dispose();
    }
}
