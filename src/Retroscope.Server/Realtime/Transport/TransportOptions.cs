namespace Retroscope.Server.Realtime.Transport;

/// <summary>Socket limits (DESIGN §c.3, §c.7). Production uses the defaults; tests shorten the timings.</summary>
public sealed record TransportOptions {
    /// <summary>The first frame must be <c>hello</c> within this time, or the socket closes with 4400.</summary>
    public TimeSpan HelloTimeout { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Protocol PING interval (R6). Browsers answer without running JS, so hidden tabs stay connected.</summary>
    public TimeSpan KeepAliveInterval { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>A socket whose PONG does not arrive within this time after a PING is aborted.</summary>
    public TimeSpan KeepAliveTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>How long a socket we closed may take to answer the close handshake before it is aborted.</summary>
    public TimeSpan CloseTimeout { get; init; } = TimeSpan.FromSeconds(2);

    public int ReceiveBufferBytes { get; init; } = 4 * 1024;

    /// <summary>Pending outbound bytes (queued and in flight) above which the socket closes with 4008.</summary>
    public long MaxOutboundBytes { get; init; } = 1024 * 1024;

    /// <summary>Live sockets per client IP (IPv6 by /64, S32); one more closes with 4429.</summary>
    public int MaxSocketsPerIp { get; init; } = 20;

    public int MaxRoomsPerConnection { get; init; } = 10;

    public int FramesPerSecond { get; init; } = 20;

    public int FrameBurst { get; init; } = 40;

    /// <summary>A connection refused by its rate limit without a one-second break for this long closes with 4429.</summary>
    public TimeSpan AbuseWindow { get; init; } = TimeSpan.FromSeconds(10);
}
