using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Integration.Tests.Transport;

// 4409 cid_in_use (R4, S16) and IConnectionRevoker (4401).
[Trait("Category", "Transport")]
public sealed class IdentityTests {
    [Fact]
    public async Task A_cid_held_by_another_live_connection_closes_with_4409() {
        await using var app = await TransportApp.StartAsync();
        var principal = Guid.CreateVersion7();
        var cid = TransportApp.NewClient();
        await using var first = await app.HelloAsync($"user:{principal}", cid);
        await using var second = await app.ConnectAsync($"user:{principal}");

        await second.SendAsync(new HelloFrame(1, cid, []));

        Assert.Equal(CloseCodes.CidInUse, await second.ReceiveCloseAsync());
        await first.SendAsync(new PingFrame(1));
        Assert.IsType<PongFrame>(await first.ReceiveAsync());
    }

    [Fact]
    public async Task A_cid_is_free_again_once_its_connection_is_gone() {
        await using var app = await TransportApp.StartAsync();
        var principal = Guid.CreateVersion7();
        var cid = TransportApp.NewClient();
        var first = await app.HelloAsync($"user:{principal}", cid);
        await first.DisposeAsync();
        await Wait.UntilAsync(() => app.Registry.Count == 0);

        await using var again = await app.HelloAsync($"user:{principal}", cid);

        Assert.Equal(1, app.Registry.Count);
    }

    [Fact]
    public async Task A_cid_last_bound_to_another_principal_closes_with_4409() {
        await using var app = await TransportApp.StartAsync();
        var cid = TransportApp.NewClient();
        var first = await app.HelloAsync($"user:{Guid.CreateVersion7()}", cid);
        await first.DisposeAsync();
        await Wait.UntilAsync(() => app.Registry.Count == 0);
        await using var other = await app.ConnectAsync($"guest:{Guid.CreateVersion7()}");

        await other.SendAsync(new HelloFrame(1, cid, []));

        Assert.Equal(CloseCodes.CidInUse, await other.ReceiveCloseAsync());
    }

    [Fact]
    public async Task Revoking_a_principal_closes_all_of_its_sockets_with_4401() {
        await using var app = await TransportApp.StartAsync();
        var principal = Guid.CreateVersion7();
        await using var tab1 = await app.HelloAsync($"user:{principal}");
        await using var tab2 = await app.HelloAsync($"user:{principal}");
        await using var bystander = await app.HelloAsync($"user:{Guid.CreateVersion7()}");

        Revoker(app).RevokePrincipal(new PrincipalId(principal));

        Assert.Equal(CloseCodes.Unauthenticated, await tab1.ReceiveCloseAsync());
        Assert.Equal(CloseCodes.Unauthenticated, await tab2.ReceiveCloseAsync());
        await bystander.SendAsync(new PingFrame(1));
        Assert.IsType<PongFrame>(await bystander.ReceiveAsync());
    }

    [Fact]
    public async Task Revoking_a_sign_in_session_closes_only_its_sockets() {
        await using var app = await TransportApp.StartAsync();
        var principal = Guid.CreateVersion7();
        var revoked = Guid.CreateVersion7();
        await using var laptop = await app.HelloAsync($"user:{principal}:{revoked}");
        await using var phone = await app.HelloAsync($"user:{principal}:{Guid.CreateVersion7()}");

        Revoker(app).RevokeAuthSession(revoked);

        Assert.Equal(CloseCodes.Unauthenticated, await laptop.ReceiveCloseAsync());
        await phone.SendAsync(new PingFrame(1));
        Assert.IsType<PongFrame>(await phone.ReceiveAsync());
    }

    [Fact]
    public async Task Connections_of_a_principal_are_listed_once_they_said_hello() {
        await using var app = await TransportApp.StartAsync();
        var principal = Guid.CreateVersion7();
        await using var silent = await app.ConnectAsync($"user:{principal}");
        await using var tab = await app.HelloAsync($"user:{principal}");

        var connections = app.Registry.ConnectionsOf(new PrincipalId(principal));

        Assert.Equal(principal, Assert.Single(connections).Viewer.Who.Id.Value);
    }

    private static IConnectionRevoker Revoker(TransportApp app) {
        return Microsoft.Extensions.DependencyInjection.ServiceProviderServiceExtensions.GetRequiredService<IConnectionRevoker>(app.App.Services);
    }
}
