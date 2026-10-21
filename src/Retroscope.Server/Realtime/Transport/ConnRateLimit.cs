namespace Retroscope.Server.Realtime.Transport;

internal enum RateVerdict : byte {
    Allowed,

    /// <summary>Refused: a command gets the retryable <c>rate_limited</c> nack.</summary>
    Limited,

    /// <summary>Refused without a break for the abuse window: the socket closes with 4429.</summary>
    Abusive,
}

/// <summary>
/// A per-connection token bucket over inbound frames (§c.7: 20/s, burst 40). Used only by the connection's receive
/// loop, so it needs no locking. A refusal streak survives as long as refusals are less than a second apart: a client
/// that keeps sending faster than the refill rate is refused a few times every second and is closed at the end of
/// the abuse window, while a short burst is only nacked.
/// </summary>
/// <remarks>
/// A frame that must get through (the <c>sub</c> that lifts a <c>rate_limited</c> refusal) borrows its token: the
/// bucket may go up to one burst into debt, which later frames repay before any is allowed. So such frames still cost
/// the rate, and over any interval the connection is let through at most the refill plus two bursts.
/// </remarks>
internal sealed class ConnRateLimit {
    private static readonly TimeSpan StreakGap = TimeSpan.FromSeconds(1);

    private readonly TimeProvider _time;
    private readonly double _perSecond;
    private readonly double _burst;
    private readonly TimeSpan _abuseWindow;
    private double _tokens;
    private long _refilledAt;
    private long _streakStart;
    private long? _lastRefusal;

    public ConnRateLimit(TimeProvider time, int perSecond, int burst, TimeSpan abuseWindow) {
        _time = time;
        _perSecond = perSecond;
        _burst = burst;
        _abuseWindow = abuseWindow;
        _tokens = burst;
        _refilledAt = time.GetTimestamp();
    }

    /// <param name="borrow">Allow the frame on credit while the debt is below one burst.</param>
    public RateVerdict Take(bool borrow = false) {
        var now = _time.GetTimestamp();
        _tokens = Math.Min(_burst, _tokens + (_time.GetElapsedTime(_refilledAt, now).TotalSeconds * _perSecond));
        _refilledAt = now;
        if (_tokens >= 1 || (borrow && _tokens > -_burst)) {
            _tokens -= 1;
            return RateVerdict.Allowed;
        }
        if (_lastRefusal is not { } last || _time.GetElapsedTime(last, now) > StreakGap) {
            _streakStart = now;
        }
        _lastRefusal = now;
        return _time.GetElapsedTime(_streakStart, now) >= _abuseWindow ? RateVerdict.Abusive : RateVerdict.Limited;
    }
}
