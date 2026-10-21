using System.Collections.Concurrent;
using Retroscope.Core.Ids;

namespace Retroscope.Server.Jira;

public sealed record JiraThrottleOptions {
    public double RequestsPerSecond { get; init; } = 8;

    public int Burst { get; init; } = 16;

    public int ConcurrentPerTeam { get; init; } = 4;

    public int ConcurrentGlobal { get; init; } = 16;

    public int BreakerThreshold { get; init; } = 5;

    public TimeSpan BreakerOpenFor { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// Outbound Jira limits (DESIGN §f): a token bucket and a concurrency cap per team, a global concurrency cap, and a
/// circuit breaker per team that opens after consecutive 5xx responses or timeouts.
/// </summary>
public sealed class JiraThrottle : IDisposable {
    private readonly JiraThrottleOptions _options;
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly SemaphoreSlim _global;
    private readonly ConcurrentDictionary<TeamId, TeamState> _teams = new();

    public JiraThrottle(JiraThrottleOptions options, TimeProvider time, Func<TimeSpan, CancellationToken, Task> delay) {
        _options = options;
        _time = time;
        _delay = delay;
        _global = new SemaphoreSlim(options.ConcurrentGlobal, options.ConcurrentGlobal);
    }

    public void Dispose() => _global.Dispose();

    /// <summary>
    /// Waits for a rate token, then a team slot, then a global slot. Dispose the lease when the request ends. A caller
    /// that gives up while waiting sent nothing to Jira, so its token goes back to the bucket.
    /// </summary>
    /// <exception cref="JiraException"><see cref="JiraErrorCode.Unavailable"/> while the team's circuit is open.</exception>
    public async ValueTask<IDisposable> AcquireAsync(TeamId team, CancellationToken cancellationToken) {
        ThrowIfOpen(team);
        var state = State(team);
        var wait = state.ReserveToken(_time.GetTimestamp(), _time, _options);
        try {
            if (wait > TimeSpan.Zero) {
                await _delay(wait, cancellationToken);
            }
            await state.Slots.WaitAsync(cancellationToken);
        } catch {
            state.ReturnToken(_options);
            throw;
        }

        try {
            await _global.WaitAsync(cancellationToken);
        } catch {
            state.Slots.Release();
            state.ReturnToken(_options);
            throw;
        }

        return new Lease(state.Slots, _global);
    }

    public void ThrowIfOpen(TeamId team) {
        if (IsOpen(team)) {
            throw new JiraException(JiraError.Unavailable);
        }
    }

    public bool IsOpen(TeamId team) =>
        _teams.TryGetValue(team, out var state) && state.IsOpen(_time.GetUtcNow());

    /// <summary>A response that is not a 5xx (a 4xx is Jira working as designed) closes the breaker's count.</summary>
    public void RecordSuccess(TeamId team) => State(team).RecordSuccess();

    /// <summary>A 5xx response or a timeout.</summary>
    public void RecordFailure(TeamId team) => State(team).RecordFailure(_time.GetUtcNow(), _options);

    private TeamState State(TeamId team) => _teams.GetOrAdd(team, static (_, options) => new TeamState(options), _options);

    private sealed class Lease(SemaphoreSlim team, SemaphoreSlim global) : IDisposable {
        private int _disposed;

        public void Dispose() {
            if (Interlocked.Exchange(ref _disposed, 1) == 0) {
                global.Release();
                team.Release();
            }
        }
    }

    private sealed class TeamState(JiraThrottleOptions options) {
        private readonly Lock _gate = new();
        private double _tokens = options.Burst;
        private long _refilledAt = -1;
        private int _consecutiveFailures;
        private DateTimeOffset _openUntil = DateTimeOffset.MinValue;

        public SemaphoreSlim Slots { get; } = new(options.ConcurrentPerTeam, options.ConcurrentPerTeam);

        // Reservation bucket: a caller takes its token now, going negative if need be, and waits until the
        // bucket has refilled to where its token would have been. Callers are served in arrival order.
        public TimeSpan ReserveToken(long now, TimeProvider time, JiraThrottleOptions options) {
            lock (_gate) {
                if (_refilledAt >= 0) {
                    var elapsed = time.GetElapsedTime(_refilledAt, now).TotalSeconds;
                    _tokens = Math.Min(options.Burst, _tokens + elapsed * options.RequestsPerSecond);
                }
                _refilledAt = now;
                _tokens -= 1;
                return _tokens >= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds(-_tokens / options.RequestsPerSecond);
            }
        }

        public void ReturnToken(JiraThrottleOptions options) {
            lock (_gate) {
                _tokens = Math.Min(options.Burst, _tokens + 1);
            }
        }

        public bool IsOpen(DateTimeOffset now) {
            lock (_gate) {
                return now < _openUntil;
            }
        }

        public void RecordSuccess() {
            lock (_gate) {
                _consecutiveFailures = 0;
            }
        }

        // After the open period one more failure re-opens the breaker at once (half-open); a success resets it.
        public void RecordFailure(DateTimeOffset now, JiraThrottleOptions options) {
            lock (_gate) {
                _consecutiveFailures++;
                if (_consecutiveFailures >= options.BreakerThreshold && now >= _openUntil) {
                    _openUntil = now + options.BreakerOpenFor;
                    _consecutiveFailures = options.BreakerThreshold - 1;
                }
            }
        }
    }
}
