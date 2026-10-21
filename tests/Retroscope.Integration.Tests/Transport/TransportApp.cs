using System.Collections.Concurrent;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Server.Hosting;
using Retroscope.Server.Realtime.Transport;

namespace Retroscope.Integration.Tests.Transport;

// The hosting pipeline and the transport on real Kestrel (loopback), so sockets, PING/PONG and TCP back-pressure are
// the real ones. Rooms are a recording gateway: the actor runtime is another unit's.
internal sealed class TransportApp : IAsyncDisposable {
    public const string Origin = "https://retro.test";
    public const string CallerHeader = "X-Test-Caller";

    private TransportApp(WebApplication app, RecordingGateway gateway, Uri socketUri, Uri httpUri) {
        App = app;
        Gateway = gateway;
        SocketUri = socketUri;
        HttpUri = httpUri;
    }

    public WebApplication App { get; }

    public RecordingGateway Gateway { get; }

    public Uri SocketUri { get; }

    public Uri HttpUri { get; }

    public ConnectionRegistry Registry => App.Services.GetRequiredService<ConnectionRegistry>();

    public ShutdownCoordinator Shutdown => App.Services.GetRequiredService<ShutdownCoordinator>();

    public static async Task<TransportApp> StartAsync(TransportOptions? options = null, bool trustLoopbackProxy = false) {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["PUBLIC_URL"] = Origin,
            ["WEB_ROOT"] = Path.Combine(Path.GetTempPath(), "rs-no-web-build"),
            ["TRUSTED_PROXY_CIDRS"] = trustLoopbackProxy ? "127.0.0.1/32" : null,
        });
        var gateway = new RecordingGateway();
        HostingModule.AddServices(builder.Services, builder.Configuration);
        TransportModule.AddServices(builder.Services, builder.Configuration);
        builder.Services.Replace(ServiceDescriptor.Singleton(options ?? new TransportOptions()));
        builder.Services.Replace(ServiceDescriptor.Singleton<IRoomGateway>(gateway));
        builder.Services.AddSingleton<ICallerResolver, TestCallerResolver>();

        var app = builder.Build();
        HostingModule.UsePipeline(app);
        HostingModule.Map(app);
        TransportModule.Map(app);
        await app.StartAsync(TestContext.Current.CancellationToken);

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var http = new Uri(address);
        return new TransportApp(app, gateway, new Uri($"ws://{http.Authority}{WsEndpoint.Path}"), http);
    }

    public Task<WsClient> ConnectAsync(string caller = "user", string? origin = Origin, string? forwardedFor = null) {
        return WsClient.ConnectAsync(this, caller, origin, forwardedFor);
    }

    public async Task<WsClient> HelloAsync(string caller = "user", ClientId? client = null, params SubCursor[] subs) {
        var socket = await ConnectAsync(caller);
        await socket.SendAsync(new HelloFrame(1, client ?? NewClient(), subs));
        Assert.IsType<WelcomeFrame>(await socket.ReceiveAsync());
        return socket;
    }

    public static ClientId NewClient() => new(Guid.CreateVersion7());

    public static RoomKey NewRoom() => RoomKey.Poker(new SessionId(Guid.CreateVersion7()));

    public async ValueTask DisposeAsync() {
        await App.StopAsync(CancellationToken.None);
        await App.DisposeAsync();
    }
}

// "user", "guest", then optionally ":<principal uuid>" and ":<auth session uuid>".
internal sealed class TestCallerResolver : ICallerResolver {
    public ValueTask<Caller?> ResolveAsync(HttpContext context) {
        if (!context.Request.Headers.TryGetValue(TransportApp.CallerHeader, out var header)) {
            return ValueTask.FromResult<Caller?>(null);
        }
        var parts = header.ToString().Split(':');
        var id = new PrincipalId(parts.Length > 1 ? Guid.Parse(parts[1]) : Guid.CreateVersion7());
        Guid? session = parts.Length > 2 ? Guid.Parse(parts[2]) : null;
        var caller = parts[0] == "guest"
            ? new Caller(new Principal(id, PrincipalKind.Guest, "Gus", null, false), false)
            : new Caller(new Principal(id, PrincipalKind.User, "Uma", null, false), true, session);
        return ValueTask.FromResult<Caller?>(caller);
    }
}

internal sealed record GatewayCall(string Kind, IClientConnection Connection, RoomKey? Room = null, object? Detail = null);

/// <summary>Records every call; <see cref="OnSubscribe"/> and <see cref="OnCommand"/> let a test answer like an actor.</summary>
internal sealed class RecordingGateway : IRoomGateway {
    private readonly ConcurrentQueue<GatewayCall> _calls = new();

    public Action<IClientConnection, RoomKey>? OnSubscribe { get; set; }

    public Action<IClientConnection, CmdFrame>? OnCommand { get; set; }

    public IReadOnlyList<GatewayCall> Calls => [.. _calls];

    public void Subscribe(IClientConnection connection, RoomKey room, Epoch? epoch, long? seq) {
        _calls.Enqueue(new GatewayCall("sub", connection, room, new SubCursor(room, epoch, seq)));
        OnSubscribe?.Invoke(connection, room);
    }

    public void Unsubscribe(IClientConnection connection, RoomKey room) {
        _calls.Enqueue(new GatewayCall("unsub", connection, room));
    }

    public void Command(IClientConnection connection, CmdFrame command) {
        _calls.Enqueue(new GatewayCall("cmd", connection, command.R, command));
        OnCommand?.Invoke(connection, command);
    }

    public void Disconnected(IClientConnection connection, IReadOnlyCollection<RoomKey> rooms) {
        _calls.Enqueue(new GatewayCall("gone", connection, null, rooms));
    }

    public IReadOnlyList<GatewayCall> Of(string kind) => [.. _calls.Where(x => x.Kind == kind)];

    public async Task<GatewayCall> WaitForAsync(string kind, Func<GatewayCall, bool>? match = null) {
        GatewayCall? found = null;
        await Wait.UntilAsync(() => (found = _calls.FirstOrDefault(x => x.Kind == kind && (match?.Invoke(x) ?? true))) is not null);
        return found!;
    }
}

internal static class Wait {
    public static readonly TimeSpan Default = TimeSpan.FromSeconds(10);

    public static async Task UntilAsync(Func<bool> condition, TimeSpan? timeout = null) {
        var deadline = DateTime.UtcNow + (timeout ?? Default);
        while (!condition()) {
            if (DateTime.UtcNow > deadline) {
                throw new TimeoutException("The condition did not hold in time.");
            }
            await Task.Delay(20, TestContext.Current.CancellationToken);
        }
    }
}

internal sealed class WsClient : IAsyncDisposable {
    private readonly byte[] _buffer = new byte[256 * 1024];

    private WsClient(ClientWebSocket socket) {
        Socket = socket;
    }

    public ClientWebSocket Socket { get; }

    public static async Task<WsClient> ConnectAsync(TransportApp app, string caller, string? origin, string? forwardedFor) {
        var socket = new ClientWebSocket();
        // Silent like a browser tab whose JS is throttled: no client PINGs, only PONG answers while a receive is pending.
        socket.Options.KeepAliveInterval = TimeSpan.Zero;
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader(TransportApp.CallerHeader, caller);
        if (origin is not null) {
            socket.Options.SetRequestHeader("Origin", origin);
        }
        if (forwardedFor is not null) {
            socket.Options.SetRequestHeader("X-Forwarded-For", forwardedFor);
        }
        await socket.ConnectAsync(app.SocketUri, TestContext.Current.CancellationToken);
        return new WsClient(socket);
    }

    public Task SendAsync(ClientFrame frame) => SendRawAsync(WireWriter.ToUtf8(frame));

    public Task SendRawAsync(string text) => SendRawAsync(System.Text.Encoding.UTF8.GetBytes(text));

    public async Task SendRawAsync(byte[] bytes, WebSocketMessageType type = WebSocketMessageType.Text) {
        await Socket.SendAsync(bytes, type, endOfMessage: true, TestContext.Current.CancellationToken);
    }

    /// <summary>The next frame; fails if the server closes instead.</summary>
    public async Task<ServerFrame> ReceiveAsync(TimeSpan? timeout = null) {
        var (bytes, close) = await ReceiveMessageAsync(timeout);
        Assert.True(bytes is not null, $"Expected a frame, got close {close}");
        var parsed = WireParser.ParseServer(bytes);
        Assert.True(parsed.Ok, parsed.Message);
        return parsed.Frame!;
    }

    /// <summary>Reads (and discards) frames until the server's close frame; returns its status code.</summary>
    public async Task<int> ReceiveCloseAsync(TimeSpan? timeout = null) {
        var deadline = DateTime.UtcNow + (timeout ?? Wait.Default);
        while (true) {
            var (_, close) = await ReceiveMessageAsync(deadline - DateTime.UtcNow);
            if (close is { } code) {
                return code;
            }
        }
    }

    /// <summary>Frames up to and including the close, so a test can see what preceded it.</summary>
    public async Task<(List<ServerFrame> Frames, int Close)> ReceiveUntilCloseAsync() {
        var frames = new List<ServerFrame>();
        while (true) {
            var (bytes, close) = await ReceiveMessageAsync(null);
            if (close is { } code) {
                return (frames, code);
            }
            frames.Add(WireParser.ParseServer(bytes).Frame!);
        }
    }

    public async Task<(byte[]? Bytes, int? Close)> ReceiveMessageAsync(TimeSpan? timeout) {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        cts.CancelAfter(timeout ?? Wait.Default);
        var length = 0;
        while (true) {
            var result = await Socket.ReceiveAsync(_buffer.AsMemory(length), cts.Token);
            if (result.MessageType == WebSocketMessageType.Close) {
                if (Socket.State == WebSocketState.CloseReceived) {
                    await Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                }
                return (null, (int)Socket.CloseStatus!.Value);
            }
            length += result.Count;
            if (result.EndOfMessage) {
                return (_buffer[..length], null);
            }
        }
    }

    public async ValueTask DisposeAsync() {
        if (Socket.State == WebSocketState.Open) {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            try {
                await Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, cts.Token);
            } catch (Exception exception) when (exception is WebSocketException or OperationCanceledException) {
                Socket.Abort();
            }
        }
        Socket.Dispose();
    }
}
