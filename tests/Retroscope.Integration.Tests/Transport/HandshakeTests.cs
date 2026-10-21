using System.Net;
using System.Net.WebSockets;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Server.Realtime.Transport;

namespace Retroscope.Integration.Tests.Transport;

[Trait("Category", "Transport")]
public sealed class HandshakeTests {
    private static readonly TransportOptions Fast = new() { HelloTimeout = TimeSpan.FromMilliseconds(300) };

    [Fact]
    public async Task Hello_is_answered_with_welcome_naming_the_connection_and_the_principal() {
        await using var app = await TransportApp.StartAsync();
        var principal = Guid.CreateVersion7();
        await using var client = await app.ConnectAsync($"guest:{principal}");

        await client.SendAsync(new HelloFrame(1, TransportApp.NewClient(), []));
        var welcome = Assert.IsType<WelcomeFrame>(await client.ReceiveAsync());

        Assert.Equal(principal, welcome.Me.Pid.Value);
        Assert.Equal(PrincipalKind.Guest, welcome.Me.Kind);
        Assert.Equal("Gus", welcome.Me.Name);
        Assert.NotEqual(Guid.Empty, welcome.Conn.Value);
        Assert.InRange(welcome.N, DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeMilliseconds(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task No_hello_within_the_timeout_closes_with_4400() {
        await using var app = await TransportApp.StartAsync(Fast);
        await using var client = await app.ConnectAsync();

        Assert.Equal(CloseCodes.ProtocolViolation, await client.ReceiveCloseAsync());
        await Wait.UntilAsync(() => app.Registry.Count == 0);
        Assert.Empty(app.Gateway.Calls);
    }

    [Fact]
    public async Task Hello_in_time_cancels_the_timeout() {
        await using var app = await TransportApp.StartAsync(Fast);
        await using var client = await app.HelloAsync();
        await Task.Delay(600, TestContext.Current.CancellationToken);

        await client.SendAsync(new PingFrame(7));

        Assert.Equal(7, Assert.IsType<PongFrame>(await client.ReceiveAsync()).C);
    }

    [Theory]
    [InlineData("https://evil.test")]
    [InlineData("http://retro.test")]
    [InlineData("https://retro.test:443")]
    [InlineData("null")]
    public async Task A_foreign_origin_closes_with_4403(string origin) {
        await using var app = await TransportApp.StartAsync();
        await using var client = await app.ConnectAsync(origin: origin);

        Assert.Equal(CloseCodes.BadOrigin, await client.ReceiveCloseAsync());
        Assert.Equal(0, app.Registry.Count);
    }

    [Fact]
    public async Task A_missing_origin_closes_with_4403() {
        await using var app = await TransportApp.StartAsync();
        await using var client = await app.ConnectAsync(origin: null);

        Assert.Equal(CloseCodes.BadOrigin, await client.ReceiveCloseAsync());
    }

    [Fact]
    public async Task An_anonymous_upgrade_is_refused_before_it_is_accepted() {
        await using var app = await TransportApp.StartAsync();
        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Origin", TransportApp.Origin);

        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(app.SocketUri, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.Unauthorized, socket.HttpStatusCode);
    }

    [Fact]
    public async Task A_plain_get_is_a_bad_request() {
        await using var app = await TransportApp.StartAsync();
        using var http = new HttpClient { BaseAddress = app.HttpUri };
        using var request = new HttpRequestMessage(HttpMethod.Get, "/ws");
        request.Headers.Add(TransportApp.CallerHeader, "user");

        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task A_frame_before_hello_closes_with_4400() {
        await using var app = await TransportApp.StartAsync();
        await using var client = await app.ConnectAsync();

        await client.SendAsync(new SubFrame(TransportApp.NewRoom()));

        Assert.Equal(CloseCodes.ProtocolViolation, await client.ReceiveCloseAsync());
        Assert.Empty(app.Gateway.Calls);
    }

    [Fact]
    public async Task A_second_hello_closes_with_4400() {
        await using var app = await TransportApp.StartAsync();
        await using var client = await app.HelloAsync();

        await client.SendAsync(new HelloFrame(1, TransportApp.NewClient(), []));

        Assert.Equal(CloseCodes.ProtocolViolation, await client.ReceiveCloseAsync());
    }

    [Fact]
    public async Task An_unknown_protocol_version_closes_with_4400() {
        await using var app = await TransportApp.StartAsync();
        await using var client = await app.ConnectAsync();

        await client.SendAsync(new HelloFrame(2, TransportApp.NewClient(), []));

        Assert.Equal(CloseCodes.ProtocolViolation, await client.ReceiveCloseAsync());
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"t":"nope"}""")]
    [InlineData("""{"t":"ping","c":1,"extra":true}""")]
    public async Task A_malformed_frame_closes_with_4400(string frame) {
        await using var app = await TransportApp.StartAsync();
        await using var client = await app.HelloAsync();

        await client.SendRawAsync(frame);

        Assert.Equal(CloseCodes.ProtocolViolation, await client.ReceiveCloseAsync());
    }

    [Fact]
    public async Task A_binary_frame_closes_with_4400() {
        await using var app = await TransportApp.StartAsync();
        await using var client = await app.HelloAsync();

        await client.SendRawAsync([1, 2, 3], WebSocketMessageType.Binary);

        Assert.Equal(CloseCodes.ProtocolViolation, await client.ReceiveCloseAsync());
    }

    [Fact]
    public async Task Ping_is_answered_with_the_client_time_and_the_server_time() {
        await using var app = await TransportApp.StartAsync();
        await using var client = await app.HelloAsync();

        await client.SendAsync(new PingFrame(123456789));
        var pong = Assert.IsType<PongFrame>(await client.ReceiveAsync());

        Assert.Equal(123456789, pong.C);
        Assert.InRange(pong.N, DateTimeOffset.UtcNow.AddSeconds(-5).ToUnixTimeMilliseconds(), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    }

    [Fact]
    public async Task A_client_close_is_answered_and_the_connection_released() {
        await using var app = await TransportApp.StartAsync();
        var client = await app.HelloAsync();

        await client.Socket.CloseAsync(WebSocketCloseStatus.NormalClosure, null, TestContext.Current.CancellationToken);

        Assert.Equal(WebSocketState.Closed, client.Socket.State);
        await Wait.UntilAsync(() => app.Registry.Count == 0);
        await app.Gateway.WaitForAsync("gone");
        await client.DisposeAsync();
    }
}
