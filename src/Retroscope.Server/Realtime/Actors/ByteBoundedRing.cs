using Retroscope.Core.Ids;

namespace Retroscope.Server.Realtime.Actors;

/// <summary>Who a projected tx frame was built for: a principal and its snapshot variant.</summary>
public readonly record struct Audience(PrincipalId Principal, bool Member);

/// <summary>
/// The recent tx frames of one room, kept as each audience's serialized <c>ev</c> array so a resuming client gets
/// exactly the events it would have seen live (R11). Bounded in bytes per room and under the global
/// <see cref="RingBudget"/>; a trimmed ring only means the client gets a snapshot instead of a replay.
/// Thread-safe, because the budget trims cold rings from other actors' threads.
/// </summary>
public sealed class ByteBoundedRing {
    /// <summary>Bookkeeping cost of one audience entry, on top of its bytes.</summary>
    private const int EntryOverhead = 48;

    private static readonly byte[] NoEvents = "[]"u8.ToArray();

    private readonly Lock _lock = new();
    private readonly Queue<Entry> _entries = new();
    private readonly int _capacity;
    private readonly RingBudget _budget;
    private long _bytes;
    private long _lastSeq;
    private long _touched;

    public ByteBoundedRing(int capacityBytes, RingBudget budget) {
        _capacity = capacityBytes;
        _budget = budget;
        budget.Register(this);
    }

    public long Bytes {
        get {
            lock (_lock) {
                return _bytes;
            }
        }
    }

    /// <summary>The oldest seq held, or the next seq when the ring is empty.</summary>
    public long FirstSeq {
        get {
            lock (_lock) {
                return _entries.TryPeek(out var first) ? first.Seq : _lastSeq + 1;
            }
        }
    }

    internal long Touched => Interlocked.Read(ref _touched);

    /// <summary>Appends the tx at <paramref name="seq"/>, which must follow the previous one.</summary>
    public void Append(long seq, IReadOnlyDictionary<Audience, byte[]> events) {
        var entry = new Entry(seq, events, SizeOf(events));
        long delta;
        lock (_lock) {
            if (seq != _lastSeq + 1 && _entries.Count > 0) {
                throw new ArgumentOutOfRangeException(nameof(seq), "Ring entries must be consecutive.");
            }
            _entries.Enqueue(entry);
            _lastSeq = seq;
            delta = entry.Bytes;
            while (_bytes + delta > _capacity && _entries.Count > 0) {
                delta -= _entries.Dequeue().Bytes;
            }
            _bytes += delta;
        }
        Interlocked.Exchange(ref _touched, _budget.NextTick());
        _budget.Changed(this, delta);
    }

    /// <summary>Keeps <paramref name="seq"/> in the ring but drops its events, because a later tx superseded them.</summary>
    public void Blank(long seq) {
        long delta = 0;
        lock (_lock) {
            foreach (var entry in _entries) {
                if (entry.Seq == seq) {
                    var blank = entry.Events.ToDictionary(x => x.Key, _ => NoEvents);
                    var size = SizeOf(blank);
                    delta = size - entry.Bytes;
                    entry.Events = blank;
                    entry.Bytes = size;
                    _bytes += delta;
                    break;
                }
            }
        }
        _budget.Changed(this, delta);
    }

    /// <summary>
    /// The <c>ev</c> arrays of every tx after <paramref name="afterSeq"/> up to <paramref name="lastSeq"/> for
    /// <paramref name="audience"/>; false when one of them is gone or was not built for that audience.
    /// </summary>
    public bool TryReplay(long afterSeq, long lastSeq, Audience audience, List<(long Seq, byte[] Events)> output) {
        lock (_lock) {
            var expected = afterSeq + 1;
            foreach (var entry in _entries) {
                if (entry.Seq < expected) {
                    continue;
                }
                if (entry.Seq > lastSeq) {
                    break;
                }
                if (entry.Seq != expected || !entry.Events.TryGetValue(audience, out var events)) {
                    output.Clear();
                    return false;
                }
                output.Add((entry.Seq, events));
                expected++;
            }
            if (expected != lastSeq + 1) {
                output.Clear();
                return false;
            }
            return true;
        }
    }

    /// <summary>Drops every entry; the next seq stays <paramref name="lastSeq"/> + 1.</summary>
    public void Reset(long lastSeq) {
        long delta;
        lock (_lock) {
            delta = -_bytes;
            _entries.Clear();
            _bytes = 0;
            _lastSeq = lastSeq;
        }
        _budget.Changed(this, delta);
    }

    internal long TrimAll() {
        long removed;
        lock (_lock) {
            removed = _bytes;
            _entries.Clear();
            _bytes = 0;
        }
        return removed;
    }

    /// <summary>Gives the ring's bytes back to the budget for good (the actor retired).</summary>
    public void Release() {
        long delta;
        lock (_lock) {
            delta = -_bytes;
            _entries.Clear();
            _bytes = 0;
        }
        _budget.Changed(this, delta);
        _budget.Unregister(this);
    }

    private static int SizeOf(IReadOnlyDictionary<Audience, byte[]> events) {
        var size = 0;
        var seen = new HashSet<byte[]>(ReferenceEqualityComparer.Instance);
        foreach (var bytes in events.Values) {
            size += EntryOverhead;
            if (seen.Add(bytes)) {
                size += bytes.Length;
            }
        }
        return size;
    }

    private sealed class Entry(long seq, IReadOnlyDictionary<Audience, byte[]> events, int bytes) {
        public long Seq { get; } = seq;

        public IReadOnlyDictionary<Audience, byte[]> Events { get; set; } = events;

        public int Bytes { get; set; } = bytes;
    }
}

/// <summary>
/// The process-wide ring budget (§c.3: 16 MB). When the rings together exceed it, the rooms that appended least
/// recently lose their rings first.
/// </summary>
public sealed class RingBudget(long maxBytes) {
    private readonly Lock _lock = new();
    private readonly HashSet<ByteBoundedRing> _rings = [];
    private long _total;
    private long _tick;

    public long TotalBytes => Interlocked.Read(ref _total);

    internal long NextTick() => Interlocked.Increment(ref _tick);

    internal void Register(ByteBoundedRing ring) {
        lock (_lock) {
            _rings.Add(ring);
        }
    }

    internal void Unregister(ByteBoundedRing ring) {
        lock (_lock) {
            _rings.Remove(ring);
        }
    }

    internal void Changed(ByteBoundedRing ring, long delta) {
        if (delta == 0) {
            return;
        }
        if (Interlocked.Add(ref _total, delta) > maxBytes) {
            TrimColdest(ring);
        }
    }

    private void TrimColdest(ByteBoundedRing current) {
        lock (_lock) {
            foreach (var ring in _rings.Where(x => x != current).OrderBy(x => x.Touched).ToList()) {
                if (Interlocked.Read(ref _total) <= maxBytes) {
                    return;
                }
                Interlocked.Add(ref _total, -ring.TrimAll());
            }
            if (Interlocked.Read(ref _total) > maxBytes && _rings.Contains(current)) {
                Interlocked.Add(ref _total, -current.TrimAll());
            }
        }
    }
}
