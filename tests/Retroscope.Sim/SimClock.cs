namespace Retroscope.Sim;

/// <summary>
/// Simulated time. Nothing moves the clock but <see cref="World"/>, and it fires due timers one at a time in
/// (due time, creation order) with the clock set to each timer's due time, waiting for every actor to go quiet
/// in between. That keeps a run deterministic although actors run on the thread pool.
/// </summary>
internal sealed class SimClock(DateTimeOffset start) {
    private readonly Lock _lock = new();
    private readonly SortedSet<SimTimer> _due = new(SimTimer.ByDueTime);
    private DateTimeOffset _now = start;
    private long _created;

    public DateTimeOffset Now {
        get {
            lock (_lock) {
                return _now;
            }
        }
    }

    /// <summary>A view for one server process; <see cref="ProcessClock.Kill"/> drops every timer it created.</summary>
    public ProcessClock ForProcess() => new(this);

    /// <summary>Runs <paramref name="callback"/> on the world's thread at <paramref name="dueAt"/> (client timers).</summary>
    public ITimer Schedule(DateTimeOffset dueAt, Func<Task> callback) {
        var timer = new SimTimer(this, null, null, null) { Async = callback };
        timer.Arm(dueAt);
        return timer;
    }

    /// <summary>The earliest armed timer due at or before <paramref name="until"/>, removed from the queue.</summary>
    public SimTimer? TakeDue(DateTimeOffset until) {
        lock (_lock) {
            if (_due.Count == 0 || _due.Min!.DueAt > until) {
                return null;
            }
            var timer = _due.Min;
            _due.Remove(timer);
            if (timer.DueAt > _now) {
                _now = timer.DueAt;
            }
            return timer;
        }
    }

    public void MoveTo(DateTimeOffset until) {
        lock (_lock) {
            if (until > _now) {
                _now = until;
            }
        }
    }

    private void Add(SimTimer timer, DateTimeOffset dueAt) {
        lock (_lock) {
            _due.Remove(timer);
            timer.DueAt = dueAt < _now ? _now : dueAt;
            timer.Order = ++_created;
            _due.Add(timer);
        }
    }

    private void Remove(SimTimer timer) {
        lock (_lock) {
            _due.Remove(timer);
        }
    }

    internal sealed class SimTimer(SimClock clock, ProcessClock? owner, TimerCallback? callback, object? state) : ITimer {
        public static readonly IComparer<SimTimer> ByDueTime = Comparer<SimTimer>.Create((x, y) => {
            var byDue = x.DueAt.CompareTo(y.DueAt);
            return byDue != 0 ? byDue : x.Order.CompareTo(y.Order);
        });

        private bool _disposed;

        public DateTimeOffset DueAt { get; set; }

        public long Order { get; set; }

        public Func<Task>? Async { get; init; }

        public void Arm(DateTimeOffset dueAt) {
            if (!_disposed) {
                clock.Add(this, dueAt);
            }
        }

        /// <summary>A process timer only enqueues a message; a client timer runs its whole reaction.</summary>
        public Task FireAsync() {
            if (_disposed || owner?.Dead == true) {
                return Task.CompletedTask;
            }
            if (Async is { } run) {
                return run();
            }
            callback!(state);
            return Task.CompletedTask;
        }

        public bool Change(TimeSpan dueTime, TimeSpan period) {
            if (period != Timeout.InfiniteTimeSpan) {
                throw new NotSupportedException("The simulation only runs one-shot timers.");
            }
            if (_disposed) {
                return false;
            }
            if (dueTime == Timeout.InfiniteTimeSpan) {
                clock.Remove(this);
            } else {
                Arm(clock.Now + dueTime);
            }
            return true;
        }

        public void Dispose() {
            _disposed = true;
            clock.Remove(this);
        }

        public ValueTask DisposeAsync() {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>The <see cref="TimeProvider"/> one server process sees.</summary>
    internal sealed class ProcessClock(SimClock clock) : TimeProvider {
        public bool Dead { get; private set; }

        public override DateTimeOffset GetUtcNow() => clock.Now;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) {
            var timer = new SimTimer(clock, this, callback, state);
            timer.Change(dueTime, period);
            return timer;
        }

        /// <summary>The process died: none of its timers fire again.</summary>
        public void Kill() => Dead = true;
    }
}
