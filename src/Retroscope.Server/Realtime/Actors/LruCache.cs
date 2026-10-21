namespace Retroscope.Server.Realtime.Actors;

/// <summary>A small least-recently-used map with an optional time to live; owned by one actor, so not thread-safe.</summary>
public sealed class LruCache<TKey, TValue>(int capacity, TimeProvider time, TimeSpan? timeToLive = null) where TKey : notnull {
    private readonly Dictionary<TKey, LinkedListNode<Entry>> _map = [];
    private readonly LinkedList<Entry> _order = new();

    public int Count => _map.Count;

    public bool TryGet(TKey key, out TValue value) {
        if (_map.TryGetValue(key, out var node)) {
            if (timeToLive is not { } ttl || time.GetUtcNow() - node.Value.StoredAt < ttl) {
                _order.Remove(node);
                _order.AddFirst(node);
                value = node.Value.Value;
                return true;
            }
            Remove(key);
        }
        value = default!;
        return false;
    }

    public void Set(TKey key, TValue value) {
        if (_map.TryGetValue(key, out var existing)) {
            _order.Remove(existing);
        }
        var node = _order.AddFirst(new Entry(key, value, time.GetUtcNow()));
        _map[key] = node;
        while (_map.Count > capacity) {
            var last = _order.Last!;
            _order.RemoveLast();
            _map.Remove(last.Value.Key);
        }
    }

    public void Remove(TKey key) {
        if (_map.Remove(key, out var node)) {
            _order.Remove(node);
        }
    }

    public void Clear() {
        _map.Clear();
        _order.Clear();
    }

    private sealed record Entry(TKey Key, TValue Value, DateTimeOffset StoredAt);
}
