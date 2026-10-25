using Retroscope.Core.Ids;

namespace Retroscope.Server.Jira;

/// <summary>
/// The one in-memory cache for everything read from Jira (DESIGN §f): least recently used entries are evicted
/// once the estimated size passes the cap (8 MB), every entry has a time to live, and an entry can carry tags
/// (the issue keys it contains) so a write to one issue drops every entry that shows it. Nothing is persisted.
/// </summary>
public sealed class JiraCache {
    public const long DefaultCapacityBytes = 8L * 1024 * 1024;

    private readonly TimeProvider _time;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _tagged = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _recency = new();
    private readonly Dictionary<(string Key, Type Type), Task> _inFlight = [];
    private long _size;
    private long _generation;

    public JiraCache(TimeProvider time, long capacityBytes = DefaultCapacityBytes) {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacityBytes);
        _time = time;
        CapacityBytes = capacityBytes;
    }

    public long CapacityBytes { get; }

    public long SizeBytes {
        get {
            lock (_gate) {
                return _size;
            }
        }
    }

    public int Count {
        get {
            lock (_gate) {
                return _entries.Count;
            }
        }
    }

    public static string Key(TeamId team, string kind, string id) => $"{team.Value:N}:{kind}:{id}";

    /// <summary>The tag every entry that shows <paramref name="issueKey"/> carries.</summary>
    public static string IssueTag(TeamId team, string issueKey) => $"{team.Value:N}:issue:{issueKey}";

    public static string TeamTag(TeamId team) => $"{team.Value:N}:team";

    public bool TryGet<T>(string key, out T value) where T : class {
        lock (_gate) {
            if (_entries.TryGetValue(key, out var node)) {
                if (node.Value.ExpiresAt <= _time.GetUtcNow()) {
                    RemoveNode(node);
                } else if (node.Value.Value is T typed) {
                    _recency.Remove(node);
                    _recency.AddFirst(node);
                    value = typed;
                    return true;
                }
            }
        }

        value = null!;
        return false;
    }

    /// <summary>Stores a value. One larger than the whole cache is not stored.</summary>
    /// <param name="sizeBytes">An estimate of the memory the value holds, e.g. the length of the Jira body it came from.</param>
    public void Set<T>(string key, T value, long sizeBytes, TimeSpan ttl, IEnumerable<string>? tags = null) where T : class {
        lock (_gate) {
            SetLocked(key, value, sizeBytes, ttl, tags);
        }
    }

    /// <summary>
    /// Returns the cached value or runs <paramref name="factory"/> once for all concurrent callers of the same key and
    /// <typeparamref name="T"/> (single-flight). The factory runs without the callers' cancellation, so one caller
    /// giving up does not fail the others; each caller stops waiting when its own token fires. A value whose key or
    /// tags were invalidated while it was being fetched is returned but not stored.
    /// </summary>
    public Task<T> GetOrCreateAsync<T>(
        string key,
        TimeSpan ttl,
        Func<Task<(T Value, long SizeBytes, IReadOnlyCollection<string>? Tags)>> factory,
        CancellationToken cancellationToken) where T : class {
        ArgumentNullException.ThrowIfNull(factory);
        return GetOrCreateAsync(key, async () => {
            var (value, size, tags) = await factory();
            return (value, size, ttl, tags);
        }, cancellationToken);
    }

    /// <summary>
    /// The same single-flight read, with the time to live chosen by the factory per value, for a value (such as a
    /// remembered failure) that must not live as long as a successful read.
    /// </summary>
    public async Task<T> GetOrCreateAsync<T>(
        string key,
        Func<Task<(T Value, long SizeBytes, TimeSpan Ttl, IReadOnlyCollection<string>? Tags)>> factory,
        CancellationToken cancellationToken) where T : class {
        ArgumentNullException.ThrowIfNull(factory);
        if (TryGet<T>(key, out var cached)) {
            return cached;
        }

        Task<T> task;
        lock (_gate) {
            if (_inFlight.TryGetValue((key, typeof(T)), out var running)) {
                task = (Task<T>)running;
            } else {
                task = FetchAsync(key, factory, _generation);
                _inFlight[(key, typeof(T))] = task;
            }
        }

        return await task.WaitAsync(cancellationToken);
    }

    public void Remove(string key) {
        lock (_gate) {
            _generation++;
            if (_entries.TryGetValue(key, out var node)) {
                RemoveNode(node);
            }
        }
    }

    public void InvalidateTag(string tag) {
        lock (_gate) {
            _generation++;
            if (!_tagged.TryGetValue(tag, out var keys)) {
                return;
            }

            foreach (var key in keys.ToArray()) {
                if (_entries.TryGetValue(key, out var node)) {
                    RemoveNode(node);
                }
            }
            _tagged.Remove(tag);
        }
    }

    private async Task<T> FetchAsync<T>(
        string key,
        Func<Task<(T Value, long SizeBytes, TimeSpan Ttl, IReadOnlyCollection<string>? Tags)>> factory,
        long generation) where T : class {
        await Task.Yield();
        try {
            var (value, size, ttl, tags) = await factory();
            lock (_gate) {
                if (_generation == generation) {
                    SetLocked(key, value, size, ttl, tags);
                }
            }
            return value;
        } finally {
            lock (_gate) {
                _inFlight.Remove((key, typeof(T)));
            }
        }
    }

    private void SetLocked<T>(string key, T value, long sizeBytes, TimeSpan ttl, IEnumerable<string>? tags) where T : class {
        if (_entries.TryGetValue(key, out var existing)) {
            RemoveNode(existing);
        }

        var size = Math.Max(1, sizeBytes) + key.Length * 2L;
        if (size > CapacityBytes || ttl <= TimeSpan.Zero) {
            return;
        }

        var tagList = tags?.Distinct(StringComparer.Ordinal).ToArray() ?? [];
        var node = _recency.AddFirst(new Entry(key, value, size, _time.GetUtcNow() + ttl, tagList));
        _entries[key] = node;
        _size += size;
        foreach (var tag in tagList) {
            if (!_tagged.TryGetValue(tag, out var keys)) {
                keys = new HashSet<string>(StringComparer.Ordinal);
                _tagged[tag] = keys;
            }
            keys.Add(key);
        }

        while (_size > CapacityBytes && _recency.Last is { } oldest) {
            RemoveNode(oldest);
        }
    }

    private void RemoveNode(LinkedListNode<Entry> node) {
        _recency.Remove(node);
        _entries.Remove(node.Value.Key);
        _size -= node.Value.Size;
        foreach (var tag in node.Value.Tags) {
            if (_tagged.TryGetValue(tag, out var keys)) {
                keys.Remove(node.Value.Key);
                if (keys.Count == 0) {
                    _tagged.Remove(tag);
                }
            }
        }
    }

    private sealed record Entry(string Key, object Value, long Size, DateTimeOffset ExpiresAt, string[] Tags);
}
