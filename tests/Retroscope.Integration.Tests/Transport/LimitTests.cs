using System.Diagnostics;
using System.Text;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Server.Realtime.Transport;

namespace Retroscope.Integration.Tests.Transport;

[Trait("Category", "Transport")]
public sealed class LimitTests {
    [Fact]
    public async Task A_frame_of_exactly_64_KiB_is_accepted() {
        await using var app = await TransportApp.StartAsync();
        await using var client = await app.HelloAsync();

        await client.SendRawAsync(Padded("""{"t":"ping","c":64}""", WireParser.MaxFrameBytes));

        Assert.Equal(64, Assert.IsType<PongFrame>(await client.ReceiveAsync()).C);
    }

    [Fact]
    public async Task A_frame_over_64_KiB_closes_with_4413() {
        await using var app = await TransportApp.StartAsync();
        await using var client = await app.HelloAsync();

        await client.SendRawAsync(Padded("""{"t":"ping","c":65}""", WireParser.MaxFrameBytes + 1));

        Assert.Equal(CloseCodes.FrameTooLarge, await client.ReceiveCloseAsync());
    }

    [Fact]
    public async Task An_oversized_hello_closes_with_4413() {
        await using var app = await TransportApp.StartAsync();
        await using var client = await app.ConnectAsync();

        await client.SendRawAsync(Padded("""{"t":"ping","c":1}""", 100_000));

        Assert.Equal(CloseCodes.FrameTooLarge, await client.ReceiveCloseAsync());
    }

    [Fact]
    public async Task Sockets_over_the_per_ip_cap_close_with_4429() {
        await using var app = await TransportApp.StartAsync(new TransportOptions { MaxSocketsPerIp = 3 }, trustLoopbackProxy: true);
        var open = new List<WsClient>();
        for (var i = 0; i < 3; i++) {
            open.Add(await app.ConnectAsync(forwardedFor: "198.51.100.7"));
        }
        // The handshake completes before the server counts the socket, so wait for the count before the one too many.
        await Wait.UntilAsync(() => app.Registry.Count == 3);

        await using var refused = await app.ConnectAsync(forwardedFor: "198.51.100.7");
        Assert.Equal(CloseCodes.RateLimited, await refused.ReceiveCloseAsync());
        await using var otherIp = await app.ConnectAsync(forwardedFor: "198.51.100.8");
        await otherIp.SendAsync(new HelloFrame(1, TransportApp.NewClient(), []));
        Assert.IsType<WelcomeFrame>(await otherIp.ReceiveAsync());

        await open[0].DisposeAsync();
        await Wait.UntilAsync(() => app.Registry.Count == 3);
        await using var again = await app.ConnectAsync(forwardedFor: "198.51.100.7");
        await again.SendAsync(new HelloFrame(1, TransportApp.NewClient(), []));
        Assert.IsType<WelcomeFrame>(await again.ReceiveAsync());
        foreach (var client in open.Skip(1)) {
            await client.DisposeAsync();
        }
    }

    [Fact]
    public async Task Ipv6_callers_share_the_cap_of_their_64_prefix() {
        await using var app = await TransportApp.StartAsync(new TransportOptions { MaxSocketsPerIp = 1 }, trustLoopbackProxy: true);
        await using var first = await app.ConnectAsync(forwardedFor: "2001:db8:1:2::1");
        await Wait.UntilAsync(() => app.Registry.Count == 1);

        await using var sameNetwork = await app.ConnectAsync(forwardedFor: "2001:db8:1:2:ffff::9");

        Assert.Equal(CloseCodes.RateLimited, await sameNetwork.ReceiveCloseAsync());
    }

    [Fact]
    public async Task Commands_over_the_rate_are_nacked_rate_limited_and_retryable() {
        await using var app = await TransportApp.StartAsync(new TransportOptions { FramesPerSecond = 1, FrameBurst = 3 });
        var room = TransportApp.NewRoom();
        app.Gateway.OnCommand = (connection, command) => connection.Send(new AckFrame(command.R, command.Q, 0));
        await using var client = await app.HelloAsync("user", null, new SubCursor(room));
        await app.Gateway.WaitForAsync("sub");

        for (var q = 1; q <= 5; q++) {
            await client.SendAsync(new CmdFrame(room, q, new PokerRoundReveal(new RoundId(Guid.CreateVersion7()))));
        }
        var answers = new List<ServerFrame>();
        for (var i = 0; i < 5; i++) {
            answers.Add(await client.ReceiveAsync());
        }

        Assert.Equal([1L, 2L, 3L], answers.OfType<AckFrame>().Select(x => x.Q));
        var nacks = answers.OfType<NackFrame>().ToList();
        Assert.Equal([4L, 5L], nacks.Select(x => x.Q));
        Assert.All(nacks, x => Assert.True(x.Code == NackCode.RateLimited && x.Retry));
    }

    [Fact]
    public async Task After_a_rate_limited_nack_later_commands_of_the_room_are_refused_until_it_is_subscribed_again() {
        await using var app = await TransportApp.StartAsync(new TransportOptions { FramesPerSecond = 1, FrameBurst = 3 });
        var room = TransportApp.NewRoom();
        var other = TransportApp.NewRoom();
        app.Gateway.OnCommand = (connection, command) => connection.Send(new AckFrame(command.R, command.Q, 0));
        await using var client = await app.HelloAsync("user", null, new SubCursor(room), new SubCursor(other));
        await app.Gateway.WaitForAsync("sub", x => x.Room == other);
        Command Reveal() => new PokerRoundReveal(new RoundId(Guid.CreateVersion7()));

        for (var q = 1; q <= 4; q++) {
            await client.SendAsync(new CmdFrame(room, q, Reveal()));
        }
        var answers = new List<ServerFrame>();
        for (var i = 0; i < 4; i++) {
            answers.Add(await client.ReceiveAsync());
        }
        Assert.Equal((4L, NackCode.RateLimited), answers.OfType<NackFrame>().Select(x => (x.Q, x.Code)).Single());
        await Task.Delay(TimeSpan.FromSeconds(1.2), TestContext.Current.CancellationToken);

        await client.SendAsync(new CmdFrame(room, 5, Reveal()));
        var refused = Assert.IsType<NackFrame>(await client.ReceiveAsync());
        Assert.Equal((5L, NackCode.RateLimited, true), (refused.Q, refused.Code, refused.Retry));
        Assert.DoesNotContain(app.Gateway.Of("cmd"), x => ((CmdFrame)x.Detail!).Q >= 4);

        await client.SendAsync(new SubFrame(room, null, null));
        await Wait.UntilAsync(() => app.Gateway.Of("sub").Count(x => x.Room == room) == 2);
        // The sub borrowed its token, so the bucket refills it before the next frame.
        await Task.Delay(TimeSpan.FromSeconds(2.2), TestContext.Current.CancellationToken);
        await client.SendAsync(new CmdFrame(room, 4, Reveal()));

        Assert.Equal(4L, Assert.IsType<AckFrame>(await client.ReceiveAsync()).Q);
    }

    // The sub that lifts a rate_limited refusal is never dropped for being over the rate, but it still costs a token:
    // alternating refused commands and subs cannot fetch a snapshot per sub.
    [Fact]
    public async Task Repeated_subs_cannot_fetch_snapshots_beyond_the_rate_limit() {
        var options = new TransportOptions { FramesPerSecond = 1, FrameBurst = 3, AbuseWindow = TimeSpan.FromMinutes(5) };
        await using var app = await TransportApp.StartAsync(options);
        var room = TransportApp.NewRoom();
        app.Gateway.OnCommand = (connection, command) => connection.Send(new AckFrame(command.R, command.Q, 0));
        await using var client = await app.HelloAsync("user", null, new SubCursor(room));
        await app.Gateway.WaitForAsync("sub");
        Command Reveal() => new PokerRoundReveal(new RoundId(Guid.CreateVersion7()));
        const int Rounds = 50;
        var started = Stopwatch.StartNew();

        for (var q = 1; q <= Rounds; q++) {
            await client.SendAsync(new SubFrame(room, null, null));
            await client.SendAsync(new CmdFrame(room, q, Reveal()));
        }
        for (var i = 0; i < Rounds; i++) {
            Assert.IsNotType<KickFrame>(await client.ReceiveAsync());
        }
        var elapsed = started.Elapsed;

        // The hello's sub, the burst, one burst of debt, and what the rate refilled while the frames were handled.
        var allowed = 1 + (2 * options.FrameBurst) + (int)Math.Ceiling(elapsed.TotalSeconds * options.FramesPerSecond);
        var subs = app.Gateway.Of("sub").Count(x => x.Room == room);
        Assert.True(subs <= allowed, $"{subs} subs reached the gateway in {elapsed}; the limit allows {allowed}");
    }

    [Fact]
    public async Task Sustained_abuse_closes_with_4429() {
        var options = new TransportOptions { FramesPerSecond = 5, FrameBurst = 5, AbuseWindow = TimeSpan.FromMilliseconds(800) };
        await using var app = await TransportApp.StartAsync(options);
        await using var client = await app.HelloAsync();
        var closing = client.ReceiveCloseAsync(TimeSpan.FromSeconds(10));

        for (var i = 0; i < 400 && !closing.IsCompleted; i++) {
            try {
                await client.SendAsync(new PingFrame(i));
            } catch (System.Net.WebSockets.WebSocketException) {
                break;
            }
            await Task.Delay(5, TestContext.Current.CancellationToken);
        }

        Assert.Equal(CloseCodes.RateLimited, await closing);
    }

    [Fact]
    public async Task A_client_that_stops_reading_is_closed_with_4008() {
        await using var app = await TransportApp.StartAsync(new TransportOptions { MaxOutboundBytes = 256 * 1024 });
        await using var client = await app.HelloAsync("user", null, new SubCursor(TransportApp.NewRoom()));
        var connection = (await app.Gateway.WaitForAsync("sub")).Connection;
        var frame = Encoding.UTF8.GetBytes($$"""{"t":"pong","c":0,"n":0,"pad":"{{new string('x', 60_000)}}"}""");

        var sent = 0;
        while (connection.Send(frame)) {
            sent++;
            Assert.True(sent < 2_000, "the queue never filled");
            if (sent % 16 == 0) {
                await Task.Delay(1, TestContext.Current.CancellationToken);
            }
        }

        Assert.Equal(CloseCodes.SlowConsumer, await client.ReceiveCloseAsync(TimeSpan.FromSeconds(30)));
        Assert.False(connection.Send(frame));
    }

    private static string Padded(string json, int bytes) {
        return json[..^1] + new string(' ', bytes - json.Length) + "}";
    }
}
