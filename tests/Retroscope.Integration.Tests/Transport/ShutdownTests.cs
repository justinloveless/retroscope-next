using System.Net;
using System.Net.WebSockets;
using Retroscope.Core.Protocol;
using Retroscope.Server.Realtime.Transport;

namespace Retroscope.Integration.Tests.Transport;

// §c.9 graceful shutdown: refuse upgrades, then bye{restart, retryMs} and 1012 on every socket (R11).
[Trait("Category", "Transport")]
public sealed class ShutdownTests {
    [Fact]
    public async Task Every_socket_gets_bye_then_1012_with_retry_ms_in_the_widened_window() {
        const int sockets = 40;
        await using var app = await TransportApp.StartAsync(new TransportOptions { MaxSocketsPerIp = sockets });
        var clients = new List<WsClient>();
        for (var i = 0; i < sockets; i++) {
            clients.Add(await app.HelloAsync());
        }
        var closes = clients.Select(x => x.ReceiveUntilCloseAsync()).ToList();

        await app.Shutdown.RunAsync(TestContext.Current.CancellationToken);

        var upper = ConnectionRegistry.RetryWindowMs(sockets);
        var retries = new List<int>();
        foreach (var close in closes) {
            var (frames, code) = await close;
            Assert.Equal(CloseCodes.Restart, code);
            var bye = Assert.IsType<ByeFrame>(Assert.Single(frames));
            Assert.Equal(ByeCode.Restart, bye.Code);
            Assert.InRange(bye.RetryMs, ConnectionRegistry.MinRetryMs, upper);
            retries.Add(bye.RetryMs);
        }
        Assert.True(retries.Distinct().Count() > 1, "retryMs is drawn per connection");
        Assert.Equal(0, app.Registry.Count);
        foreach (var client in clients) {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task Upgrades_are_refused_once_shutdown_has_begun() {
        await using var app = await TransportApp.StartAsync();
        await app.Shutdown.RunAsync(TestContext.Current.CancellationToken);
        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Origin", TransportApp.Origin);
        socket.Options.SetRequestHeader(TransportApp.CallerHeader, "user");

        await Assert.ThrowsAsync<WebSocketException>(() => socket.ConnectAsync(app.SocketUri, TestContext.Current.CancellationToken));

        Assert.Equal(HttpStatusCode.ServiceUnavailable, socket.HttpStatusCode);
    }

    [Fact]
    public async Task An_upgrade_that_raced_past_the_check_is_closed_with_1012() {
        await using var app = await TransportApp.StartAsync();
        app.Registry.StopAccepting();

        await using var client = await app.ConnectAsync();

        Assert.Equal(CloseCodes.Restart, await client.ReceiveCloseAsync());
    }

    [Fact]
    public async Task Stopping_the_host_closes_live_sockets_with_1012() {
        var app = await TransportApp.StartAsync();
        await using var client = await app.HelloAsync();
        var closing = client.ReceiveUntilCloseAsync();

        await app.DisposeAsync();

        var (frames, code) = await closing;
        Assert.Equal(CloseCodes.Restart, code);
        Assert.IsType<ByeFrame>(Assert.Single(frames));
    }

    [Fact]
    public async Task A_restart_from_an_actor_sends_bye_and_1012() {
        await using var app = await TransportApp.StartAsync();
        app.Gateway.OnSubscribe = (connection, _) => connection.Restart();
        await using var client = await app.HelloAsync("user", null, new SubCursor(TransportApp.NewRoom()));

        var (frames, code) = await client.ReceiveUntilCloseAsync();

        Assert.Equal(CloseCodes.Restart, code);
        Assert.InRange(Assert.IsType<ByeFrame>(Assert.Single(frames)).RetryMs, ConnectionRegistry.MinRetryMs, ConnectionRegistry.RetryWindowMs(1));
    }

    [Theory]
    [InlineData(0, 500)]
    [InlineData(1, 515)]
    [InlineData(100, 2000)]
    [InlineData(500, 8000)]
    [InlineData(100_000, 8000)]
    public void The_retry_window_widens_with_the_live_count_up_to_8_s(int live, int upper) {
        Assert.Equal(upper, ConnectionRegistry.RetryWindowMs(live));
    }
}
