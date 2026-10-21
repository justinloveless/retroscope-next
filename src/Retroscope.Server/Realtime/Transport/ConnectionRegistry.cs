using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Server.Realtime.Transport;

internal enum Admission : byte {
    Accepted,
    Stopping,
    TooManyFromIp,
}

/// <summary>
/// Every live socket: the per-IP socket cap, the client-id bindings behind 4409 <c>cid_in_use</c> (R4, S16),
/// revocation (4401) and the graceful-shutdown close.
/// </summary>
public sealed class ConnectionRegistry : IConnectionRevoker {
    /// <summary><c>bye.retryMs</c> is drawn from 0.5 s up to a bound that grows with the live count, at most 8 s (R11).</summary>
    public const int MinRetryMs = 500;

    public const int MaxRetryMs = 8000;

    public const int RetryMsPerConnection = 15;

    private const int RecentClientCapacity = 16_384;

    private readonly Lock _lock = new();
    private readonly Dictionary<ConnectionId, Connection> _connections = [];
    private readonly Dictionary<string, int> _perIp = new(StringComparer.Ordinal);
    private readonly Dictionary<ClientId, Connection> _clients = [];
    private readonly RecentClients _recent = new(RecentClientCapacity);
    private readonly TransportOptions _options;
    private bool _accepting = true;

    public ConnectionRegistry(TransportOptions options) {
        _options = options;
    }

    public int Count {
        get {
            lock (_lock) {
                return _connections.Count;
            }
        }
    }

    /// <summary>The upper end of the <c>bye.retryMs</c> window for <paramref name="liveConnections"/> sockets.</summary>
    public static int RetryWindowMs(int liveConnections) {
        return (int)Math.Min(MaxRetryMs, MinRetryMs + ((long)liveConnections * RetryMsPerConnection));
    }

    /// <summary>Spreads reconnects after a restart so they do not arrive as one synchronized storm.</summary>
    public int DrawRetryMs() => Random.Shared.Next(MinRetryMs, RetryWindowMs(Count) + 1);

    /// <summary>Live connections of a principal, e.g. to send <c>presence.update</c> after a rename.</summary>
    public IReadOnlyList<IClientConnection> ConnectionsOf(PrincipalId principal) {
        lock (_lock) {
            return [.. _connections.Values.Where(x => x.Principal.Id == principal && x.Client is not null)];
        }
    }

    public void RevokePrincipal(PrincipalId principal) {
        CloseWhere(x => x.Principal.Id == principal);
    }

    public void RevokeAuthSession(Guid authSessionId) {
        CloseWhere(x => x.AuthSessionId == authSessionId);
    }

    /// <summary>Refuses every later connection; one that raced past the upgrade check is closed with 1012.</summary>
    public void StopAccepting() {
        lock (_lock) {
            _accepting = false;
        }
    }

    /// <summary>Sends <c>bye</c> and closes every socket with 1012, then waits until they are gone (or aborted).</summary>
    public async Task CloseAllAsync(CancellationToken cancellationToken) {
        Connection[] connections;
        lock (_lock) {
            _accepting = false;
            connections = [.. _connections.Values];
        }
        foreach (var connection in connections) {
            connection.Restart();
        }
        await Task.WhenAll(connections.Select(x => x.Finished)).WaitAsync(cancellationToken);
    }

    internal Admission TryAdd(Connection connection) {
        lock (_lock) {
            if (!_accepting) {
                return Admission.Stopping;
            }
            var sockets = _perIp.GetValueOrDefault(connection.IpKey);
            if (sockets >= _options.MaxSocketsPerIp) {
                return Admission.TooManyFromIp;
            }
            _perIp[connection.IpKey] = sockets + 1;
            _connections.Add(connection.Id, connection);
            return Admission.Accepted;
        }
    }

    internal void Remove(Connection connection) {
        lock (_lock) {
            if (!_connections.Remove(connection.Id)) {
                return;
            }
            var sockets = _perIp[connection.IpKey] - 1;
            if (sockets == 0) {
                _perIp.Remove(connection.IpKey);
            } else {
                _perIp[connection.IpKey] = sockets;
            }
            if (connection.Client is { } client && _clients.TryGetValue(client, out var bound) && bound == connection) {
                _clients.Remove(client);
            }
        }
    }

    /// <summary>
    /// Binds the tab's client id to this connection. Refused while another live connection holds it, or when it was
    /// last bound to a different principal: two tabs sharing a cid would corrupt each other's command cursors.
    /// </summary>
    internal bool TryBindClient(Connection connection, ClientId client) {
        lock (_lock) {
            if (_clients.ContainsKey(client)) {
                return false;
            }
            if (_recent.TryGet(client, out var principal) && principal != connection.Principal.Id) {
                return false;
            }
            _clients.Add(client, connection);
            _recent.Set(client, connection.Principal.Id);
            return true;
        }
    }

    private void CloseWhere(Func<Connection, bool> predicate) {
        Connection[] matches;
        lock (_lock) {
            matches = [.. _connections.Values.Where(predicate)];
        }
        foreach (var connection in matches) {
            connection.Close(CloseCodes.Unauthenticated, "revoked", urgent: true);
        }
    }

    /// <summary>The principal each recently seen client id was bound to, least recently bound evicted first.</summary>
    private sealed class RecentClients(int capacity) {
        private readonly Dictionary<ClientId, LinkedListNode<(ClientId Client, PrincipalId Principal)>> _index = [];
        private readonly LinkedList<(ClientId Client, PrincipalId Principal)> _order = new();

        public bool TryGet(ClientId client, out PrincipalId principal) {
            if (_index.TryGetValue(client, out var node)) {
                principal = node.Value.Principal;
                return true;
            }
            principal = default;
            return false;
        }

        public void Set(ClientId client, PrincipalId principal) {
            if (_index.Remove(client, out var existing)) {
                _order.Remove(existing);
            }
            _index[client] = _order.AddFirst((client, principal));
            if (_index.Count > capacity) {
                var oldest = _order.Last!;
                _order.RemoveLast();
                _index.Remove(oldest.Value.Client);
            }
        }
    }
}
