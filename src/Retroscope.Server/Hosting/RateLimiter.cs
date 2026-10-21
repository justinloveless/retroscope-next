namespace Retroscope.Server.Hosting;

/// <summary>
/// In-process token buckets for every limit, held in one LRU bounded at <see cref="DefaultCapacity"/> keys (S13):
/// a flood of fresh IPs evicts the oldest buckets instead of growing memory. An evicted key starts again full,
/// which only ever errs towards allowing a request.
/// </summary>
public sealed class RateLimiter {
    public const int DefaultCapacity = 100_000;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, LinkedListNode<Bucket>> _buckets = new(StringComparer.Ordinal);
    private readonly LinkedList<Bucket> _recency = new();
    private readonly TimeProvider _time;
    private readonly int _capacity;

    public RateLimiter(TimeProvider time, int capacity = DefaultCapacity) {
        ArgumentOutOfRangeException.ThrowIfLessThan(capacity, 1);
        _time = time;
        _capacity = capacity;
    }

    public int Count {
        get {
            lock (_lock) {
                return _buckets.Count;
            }
        }
    }

    /// <summary>
    /// Takes one token from every bucket the limit's rules name, or from none: a request refused by one rule is not
    /// charged to the others.
    /// </summary>
    /// <param name="retryAfter">When refused, how long until every refusing bucket holds a token again.</param>
    public bool TryAcquire(RateLimit limit, RateKeys keys, out TimeSpan retryAfter) {
        retryAfter = TimeSpan.Zero;
        if (limit.Rules.Count == 0) {
            return true;
        }
        var now = _time.GetTimestamp();
        lock (_lock) {
            var buckets = new Bucket?[limit.Rules.Count];
            var allowed = true;
            for (var i = 0; i < limit.Rules.Count; i++) {
                var rule = limit.Rules[i];
                var key = Key(limit, rule, keys);
                if (key is null) {
                    continue;
                }
                var bucket = Touch(key, rule);
                bucket.Refill(rule, now, _time);
                buckets[i] = bucket;
                if (bucket.Tokens < 1) {
                    allowed = false;
                    var wait = TimeSpan.FromTicks((long)((1 - bucket.Tokens) * rule.Window.Ticks / rule.Limit));
                    retryAfter = wait > retryAfter ? wait : retryAfter;
                }
            }
            if (allowed) {
                foreach (var bucket in buckets) {
                    if (bucket is not null) {
                        bucket.Tokens -= 1;
                    }
                }
            }
            return allowed;
        }
    }

    private static string? Key(RateLimit limit, RateRule rule, RateKeys keys) {
        var value = rule.Scope switch {
            RateScope.Ip => keys.Ip ?? "unknown",
            RateScope.Principal => keys.Principal,
            RateScope.Subject => keys.Subject,
            _ => "*",
        };
        return value is null ? null : $"{limit.Name}|{rule.Scope}|{rule.Limit}/{rule.Window.Ticks}|{value}";
    }

    private Bucket Touch(string key, RateRule rule) {
        if (_buckets.TryGetValue(key, out var node)) {
            _recency.Remove(node);
            _recency.AddFirst(node);
            return node.Value;
        }
        if (_buckets.Count >= _capacity) {
            var oldest = _recency.Last!;
            _recency.RemoveLast();
            _buckets.Remove(oldest.Value.Key);
        }
        var bucket = new Bucket(key, rule.Limit, _time.GetTimestamp());
        _buckets[key] = _recency.AddFirst(bucket);
        return bucket;
    }

    private sealed class Bucket(string key, double tokens, long updatedAt) {
        public string Key { get; } = key;

        public double Tokens { get; set; } = tokens;

        private long UpdatedAt { get; set; } = updatedAt;

        public void Refill(RateRule rule, long now, TimeProvider time) {
            var elapsed = time.GetElapsedTime(UpdatedAt, now);
            UpdatedAt = now;
            if (elapsed > TimeSpan.Zero) {
                Tokens = Math.Min(rule.Limit, Tokens + (elapsed.Ticks * (double)rule.Limit / rule.Window.Ticks));
            }
        }
    }
}
