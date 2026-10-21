namespace Retroscope.Server.Hosting;

/// <summary>The graceful-shutdown steps of §c.9, run in this order.</summary>
public enum ShutdownPhase {
    /// <summary>Refuse new <c>/ws</c> upgrades.</summary>
    StopAccepting,

    /// <summary>Post <c>Shutdown</c> to every actor: finish the current command, persist spotlight, send <c>bye</c>.</summary>
    DrainActors,

    /// <summary>Close the remaining sockets with 1012.</summary>
    CloseSockets,

    /// <summary>Release the leader lease.</summary>
    ReleaseLease,
}

/// <summary>
/// Runs the registered shutdown steps phase by phase (steps of one phase concurrently) on SIGTERM, before any hosted
/// service stops. Compose gives the container 8 s (<c>stop_grace_period</c>), so the whole run is bounded by the
/// host's shutdown timeout, and a failing step is logged and skipped rather than blocking the ones after it.
/// </summary>
public sealed class ShutdownCoordinator(ILogger<ShutdownCoordinator> logger) : IHostedLifecycleService {
    public static readonly TimeSpan GracePeriod = TimeSpan.FromSeconds(7);

    private readonly Lock _lock = new();
    private readonly List<(ShutdownPhase Phase, int Order, Func<CancellationToken, Task> Step)> _steps = [];
    private volatile bool _stopping;

    /// <summary>True from the moment shutdown begins; <c>/healthz</c> turns 503 and new sockets are refused.</summary>
    public bool IsStopping => _stopping;

    public void Register(ShutdownPhase phase, Func<CancellationToken, Task> step) {
        lock (_lock) {
            _steps.Add((phase, _steps.Count, step));
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken) {
        _stopping = true;
        (ShutdownPhase Phase, int Order, Func<CancellationToken, Task> Step)[] steps;
        lock (_lock) {
            steps = [.. _steps.OrderBy(x => x.Phase).ThenBy(x => x.Order)];
        }
        foreach (var phase in steps.GroupBy(x => x.Phase)) {
            var tasks = phase.Select(x => RunStepAsync(phase.Key, x.Step, cancellationToken));
            await Task.WhenAll(tasks);
        }
    }

    private async Task RunStepAsync(ShutdownPhase phase, Func<CancellationToken, Task> step, CancellationToken cancellationToken) {
        try {
            await step(cancellationToken);
        } catch (Exception exception) when (exception is not OutOfMemoryException) {
            HostingLog.ShutdownStepFailed(logger, exception, phase);
        }
    }

    public Task StoppingAsync(CancellationToken cancellationToken) => RunAsync(cancellationToken);

    public Task StartingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
