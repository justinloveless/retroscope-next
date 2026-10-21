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
    /// charged to the others, and a refused request neither creates nor refreshes any bucket, so a caller over one
    /// budget cannot grow the table with fresh keys for another rule.
    /// </summary>
    /// <param name="retryAfter">When refused, how long until every refusing bucket holds a token again.</param>
    public bool TryAcquire(RateLimit limit, RateKeys keys, out TimeSpan retryAfter) {
        retryAfter = TimeSpan.Zero;
        if (limit.Rules.Count == 0) {
            return true;
        }
        var now = _time.GetTimestamp();
        lock (_lock) {
            var keyed = new string?[limit.Rules.Count];
            var refused = false;
            for (var i = 0; i < limit.Rules.Count; i++) {
                var rule = limit.Rules[i];
                var key = keyed[i] = Key(limit, rule, keys);
                if (key is null || !_buckets.TryGetValue(key, out var node)) {
                    continue;
                }
                var tokens = node.Value.TokensAt(rule, now, _time);
                if (tokens < 1) {
                    refused = true;
                    var wait = TimeSpan.FromTicks((long)((1 - tokens) * rule.Window.Ticks / rule.Limit));
                    retryAfter = wait > retryAfter ? wait : retryAfter;
                }
            }
            if (refused) {
                return false;
            }
            for (var i = 0; i < limit.Rules.Count; i++) {
                if (keyed[i] is { } key) {
                    var bucket = Touch(key, limit.Rules[i]);
                    bucket.Refill(limit.Rules[i], now, _time);
                    bucket.Tokens -= 1;
                }
            }
            return true;
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

        public double TokensAt(RateRule rule, long now, TimeProvider time) {
            var elapsed = time.GetElapsedTime(UpdatedAt, now);
            return elapsed > TimeSpan.Zero
                ? Math.Min(rule.Limit, Tokens + (elapsed.Ticks * (double)rule.Limit / rule.Window.Ticks))
                : Tokens;
        }

        public void Refill(RateRule rule, long now, TimeProvider time) {
            Tokens = TokensAt(rule, now, time);
            UpdatedAt = now;
        }
    }
}
