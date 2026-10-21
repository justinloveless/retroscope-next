using System.Net;
using System.Net.Sockets;

namespace Retroscope.Integration.Tests.Leadership;

/// <summary>
/// A TCP relay in front of Postgres that counts round trips: a client flight is everything the client sends
/// before the server next answers. ReadyForQuery messages would overcount, because a pipelined simple-query
/// <c>BEGIN</c> gets its own ReadyForQuery in the same flight.
/// </summary>
internal sealed class RoundTripProxy : IAsyncDisposable {
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
    private readonly string _upstreamHost;
    private readonly int _upstreamPort;
    private readonly CancellationTokenSource _stop = new();
    private readonly List<Task> _pumps = [];
    private readonly Lock _gate = new();
    private bool _serverSpokeLast = true;
    private int _flights;

    public RoundTripProxy(string upstreamHost, int upstreamPort) {
        _upstreamHost = upstreamHost;
        _upstreamPort = upstreamPort;
        _listener.Start();
        _pumps.Add(AcceptAsync());
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    public int RoundTrips {
        get {
            lock (_gate) {
                return _flights;
            }
        }
    }

    public async ValueTask DisposeAsync() {
        await _stop.CancelAsync();
        _listener.Stop();
        try {
            await Task.WhenAll(_pumps);
        } catch (Exception e) when (e is OperationCanceledException or SocketException or IOException or ObjectDisposedException) {
        }
        _stop.Dispose();
    }

    private async Task AcceptAsync() {
        while (!_stop.IsCancellationRequested) {
            var client = await _listener.AcceptTcpClientAsync(_stop.Token);
            var server = new TcpClient();
            await server.ConnectAsync(_upstreamHost, _upstreamPort, _stop.Token);
            _pumps.Add(RelayAsync(client.GetStream(), server.GetStream(), fromServer: false));
            _pumps.Add(RelayAsync(server.GetStream(), client.GetStream(), fromServer: true));
        }
    }

    private async Task RelayAsync(NetworkStream from, NetworkStream to, bool fromServer) {
        var buffer = new byte[16384];
        while (true) {
            var read = await from.ReadAsync(buffer, _stop.Token);
            if (read == 0) {
                to.Socket.Shutdown(SocketShutdown.Send);
                return;
            }
            lock (_gate) {
                if (!fromServer && _serverSpokeLast) {
                    _flights++;
                }
                _serverSpokeLast = fromServer;
            }
            await to.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
        }
    }
}
