using Microsoft.Extensions.Logging.Abstractions;
using Retroscope.Server.Hosting;

namespace Retroscope.Integration.Tests.Hosting;

[Trait("Category", "Hosting")]
public sealed class ShutdownCoordinatorTests {
    [Fact]
    public async Task Steps_run_phase_by_phase_whatever_the_registration_order() {
        var coordinator = new ShutdownCoordinator(NullLogger<ShutdownCoordinator>.Instance);
        var log = new List<string>();
        coordinator.Register(ShutdownPhase.ReleaseLease, _ => Record(log, "lease"));
        coordinator.Register(ShutdownPhase.CloseSockets, _ => Record(log, "sockets"));
        coordinator.Register(ShutdownPhase.DrainActors, async _ => {
            await Task.Delay(20, TestContext.Current.CancellationToken);
            await Record(log, "actors");
        });
        coordinator.Register(ShutdownPhase.StopAccepting, _ => Record(log, "upgrades"));

        Assert.False(coordinator.IsStopping);
        await coordinator.StoppingAsync(TestContext.Current.CancellationToken);

        Assert.True(coordinator.IsStopping);
        Assert.Equal(["upgrades", "actors", "sockets", "lease"], log);
    }

    [Fact]
    public async Task A_failing_step_does_not_stop_the_later_phases() {
        var coordinator = new ShutdownCoordinator(NullLogger<ShutdownCoordinator>.Instance);
        var log = new List<string>();
        coordinator.Register(ShutdownPhase.DrainActors, _ => throw new InvalidOperationException("actor stuck"));
        coordinator.Register(ShutdownPhase.ReleaseLease, _ => Record(log, "lease"));

        await coordinator.RunAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["lease"], log);
    }

    private static Task Record(List<string> log, string step) {
        lock (log) {
            log.Add(step);
        }
        return Task.CompletedTask;
    }
}
