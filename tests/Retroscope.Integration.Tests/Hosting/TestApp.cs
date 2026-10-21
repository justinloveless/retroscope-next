using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Retroscope.Core.Ids;
using Retroscope.Core.Room;
using Retroscope.Server.Hosting;

namespace Retroscope.Integration.Tests.Hosting;

// The hosting pipeline on a TestServer with test endpoints next to the real ones. The real Program is used only
// where the full route table matters (route inventory), because its other modules belong to other units.
internal sealed class TestApp : IAsyncDisposable {
    public const string PublicUrl = "https://retro.test";
    public const string PeerHeader = "X-Test-Peer";
    public const string CallerHeader = "X-Test-Caller";

    private TestApp(WebApplication app) {
        App = app;
        Client = app.GetTestClient();
    }

    public WebApplication App { get; }

    public HttpClient Client { get; }

    public IServiceProvider Services => App.Services;

    public static async Task<TestApp> StartAsync(
        Dictionary<string, string?>? settings = null,
        Action<IServiceCollection>? services = null,
        Action<WebApplication>? map = null) {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        var configuration = new Dictionary<string, string?> {
            ["PUBLIC_URL"] = PublicUrl,
            ["WEB_ROOT"] = Path.Combine(Path.GetTempPath(), "rs-no-web-build"),
        };
        foreach (var (key, value) in settings ?? []) {
            configuration[key] = value;
        }
        builder.Configuration.AddInMemoryCollection(configuration);
        HostingModule.AddServices(builder.Services, builder.Configuration);
        builder.Services.AddSingleton<IStartupFilter, PeerFilter>();
        builder.Services.AddSingleton<ICallerResolver, HeaderCallerResolver>();
        services?.Invoke(builder.Services);

        var app = builder.Build();
        HostingModule.UsePipeline(app);
        HostingModule.Map(app);
        map?.Invoke(app);
        await app.StartAsync(TestContext.Current.CancellationToken);
        return new TestApp(app);
    }

    public static HttpRequestMessage Get(string path, string? peer = null) {
        var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (peer is not null) {
            request.Headers.Add(PeerHeader, peer);
        }
        return request;
    }

    public async ValueTask DisposeAsync() {
        Client.Dispose();
        await App.DisposeAsync();
    }

    // TestServer leaves RemoteIpAddress empty; this stands in for the TCP peer, ahead of the whole pipeline.
    private sealed class PeerFilter : IStartupFilter {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app => {
            app.Use((context, nextMiddleware) => {
                context.Connection.RemoteIpAddress = context.Request.Headers.TryGetValue(PeerHeader, out var peer)
                    ? IPAddress.Parse(peer.ToString())
                    : IPAddress.Parse("203.0.113.9");
                return nextMiddleware(context);
            });
            next(app);
        };
    }
}

// "user", "unverified", "admin", "guest", optionally ":<uuid>" for a fixed principal id.
internal sealed class HeaderCallerResolver : ICallerResolver {
    public ValueTask<Caller?> ResolveAsync(HttpContext context) {
        if (!context.Request.Headers.TryGetValue(TestApp.CallerHeader, out var header)) {
            return ValueTask.FromResult<Caller?>(null);
        }
        var parts = header.ToString().Split(':', 2);
        var id = new PrincipalId(parts.Length == 2 ? Guid.Parse(parts[1]) : Guid.CreateVersion7());
        var caller = parts[0] switch {
            "guest" => new Caller(new Principal(id, PrincipalKind.Guest, "Guest", null, false), false),
            "unverified" => new Caller(new Principal(id, PrincipalKind.User, "Una", null, false), false),
            "admin" => new Caller(new Principal(id, PrincipalKind.User, "Ada", null, true), true),
            _ => new Caller(new Principal(id, PrincipalKind.User, "Uma", null, false), true),
        };
        return ValueTask.FromResult<Caller?>(caller);
    }
}

internal sealed class FakeLeader(bool isLeader) : ILeaderEpoch {
    public bool IsLeader { get; set; } = isLeader;

    public long Current => 1;
}
