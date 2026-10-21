namespace Retroscope.Server.Realtime.Actors;

/// <summary>Limits and timings of the actor runtime (§c.7); tests shrink them.</summary>
public sealed record ActorOptions {
    public static ActorOptions Default { get; } = new();

    /// <summary>Client commands and new subscriptions queued per room before <c>server_busy</c> (R10, R26).</summary>
    public int ExternalQueueCapacity { get; init; } = 256;

    public int RingBytesPerRoom { get; init; } = 256 * 1024;

    /// <summary>All rooms' rings together; the coldest rooms are trimmed first when it is exceeded (R11).</summary>
    public long RingBytesGlobal { get; init; } = 16 * 1024 * 1024;

    /// <summary>Snapshot documents built at the same time, process-wide (R11).</summary>
    public int SnapshotConcurrency { get; init; } = 4;

    /// <summary>A principal or a tab counts as present this long after its last connection closed.</summary>
    public TimeSpan PresenceGrace { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>An actor with no subscribers and no armed timers retires after this long (R17).</summary>
    public TimeSpan IdleEviction { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a message waits for a room whose key is being purged to start again; past it the caller gets a
    /// retryable <c>server_busy</c> (R18).
    /// </summary>
    public TimeSpan IncarnationWait { get; init; } = TimeSpan.FromSeconds(5);

    public int ConnectionsPerPrincipal { get; init; } = 6;

    /// <summary>Recent <c>(cid, q) → payload hash</c> entries per room, for <c>cid_reuse</c> (§c.6).</summary>
    public int RecentHashes { get; init; } = 256;

    public int CursorCacheSize { get; init; } = 4096;

    /// <summary>The database stays authoritative; cached cursors are dropped after this long (R27).</summary>
    public TimeSpan CursorTtl { get; init; } = TimeSpan.FromMinutes(15);
}
