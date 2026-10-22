namespace Retroscope.Server.Realtime.Actors;

/// <summary>
/// One <see cref="ITimer"/> per logical key, each with a generation. A callback only reports
/// <c>(key, generation)</c> (the actor turns it into a message on its unbounded internal queue, never blocking a
/// thread-pool thread); <see cref="TryTake"/> ignores a stale generation, so a timer that fires after it was
/// replaced or cancelled does nothing. A request identical to one that already fired is not armed again, so a
/// timer fires at most once per request even when the domain still asks for it afterwards.
/// </summary>
public sealed class ActorTimers<TKey, TPayload>(TimeProvider time, Action<TKey, long> fired) where TKey : notnull {
    private readonly Dictionary<TKey, Armed> _armed = [];
    private readonly Dictionary<TKey, (DateTimeOffset DueAt, TPayload Payload)> _fired = [];
    private long _generation;

    public int Count => _armed.Count;

    public IEnumerable<TKey> Keys => _armed.Keys;

    public bool IsArmed(TKey key) => _armed.ContainsKey(key);

    /// <summary>Arms <paramref name="key"/>; an identical request leaves the armed timer alone.</summary>
    public void Arm(TKey key, DateTimeOffset dueAt, TPayload payload) {
        if (_armed.TryGetValue(key, out var armed)) {
            if (Same(armed.DueAt, armed.Payload, dueAt, payload)) {
                return;
            }
            armed.Timer.Dispose();
        } else if (_fired.TryGetValue(key, out var fired)) {
            if (Same(fired.DueAt, fired.Payload, dueAt, payload)) {
                return;
            }
            _fired.Remove(key);
        }
        var generation = ++_generation;
        var delay = dueAt - time.GetUtcNow();
        // Timers count whole milliseconds (a fraction is truncated), so round up rather than fire before dueAt.
        var timer = time.CreateTimer(
            _ => fired(key, generation),
            null,
            delay > TimeSpan.Zero ? TimeSpan.FromMilliseconds(Math.Ceiling(delay.TotalMilliseconds)) : TimeSpan.Zero,
            Timeout.InfiniteTimeSpan);
        _armed[key] = new Armed(timer, generation, dueAt, payload);
    }

    public void Cancel(TKey key) {
        _fired.Remove(key);
        if (_armed.Remove(key, out var armed)) {
            armed.Timer.Dispose();
        }
    }

    /// <summary>
    /// Removes and returns the payload when <paramref name="generation"/> is still the armed one and its time has come.
    /// A timer runs on the monotonic clock, not the wall clock the domain compares deadlines with, so it can fire a
    /// moment early. The domain would decide such an input as a no-op, and an identical request is never armed again,
    /// so an early fire is armed again for the remainder instead of taken.
    /// </summary>
    public bool TryTake(TKey key, long generation, out TPayload payload) {
        if (_armed.TryGetValue(key, out var armed) && armed.Generation == generation) {
            _armed.Remove(key);
            armed.Timer.Dispose();
            if (time.GetUtcNow() < armed.DueAt) {
                Arm(key, armed.DueAt, armed.Payload);
                payload = default!;
                return false;
            }
            _fired[key] = (armed.DueAt, armed.Payload);
            payload = armed.Payload;
            return true;
        }
        payload = default!;
        return false;
    }

    /// <summary>Arms every wanted timer and cancels every armed key that is no longer wanted.</summary>
    public void Reconcile(IEnumerable<(TKey Key, DateTimeOffset DueAt, TPayload Payload)> wanted) {
        var keep = new HashSet<TKey>();
        foreach (var (key, dueAt, payload) in wanted) {
            keep.Add(key);
            Arm(key, dueAt, payload);
        }
        foreach (var key in _armed.Keys.Concat(_fired.Keys).Where(x => !keep.Contains(x)).ToList()) {
            Cancel(key);
        }
    }

    public void Clear() {
        foreach (var armed in _armed.Values) {
            armed.Timer.Dispose();
        }
        _armed.Clear();
        _fired.Clear();
    }

    private static bool Same(DateTimeOffset dueAt, TPayload payload, DateTimeOffset otherDueAt, TPayload otherPayload) {
        return dueAt == otherDueAt && EqualityComparer<TPayload>.Default.Equals(payload, otherPayload);
    }

    private sealed record Armed(ITimer Timer, long Generation, DateTimeOffset DueAt, TPayload Payload);
}
