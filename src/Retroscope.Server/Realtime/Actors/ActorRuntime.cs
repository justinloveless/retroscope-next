using Retroscope.Server.Data.Repos;

namespace Retroscope.Server.Realtime.Actors;

/// <summary>What every actor of the process shares: options, clock, the ring budget and the snapshot gate.</summary>
/// <param name="visits">Told about every admitted subscription to a room of a team; none in hosts without teams.</param>
public sealed class ActorRuntime(ActorOptions options, TimeProvider time, ILoggerFactory loggers, IRoomVisits? visits = null) {
    public ActorOptions Options { get; } = options;

    public IRoomVisits? Visits { get; } = visits;

    public TimeProvider Time { get; } = time;

    public ILoggerFactory Loggers { get; } = loggers;

    public RingBudget RingBudget { get; } = new(options.RingBytesGlobal);

    /// <summary>Bounds how many shared snapshot documents are built at once, so a reconnect storm stays flat (R11).</summary>
    public SemaphoreSlim SnapshotGate { get; } = new(options.SnapshotConcurrency, options.SnapshotConcurrency);
}
