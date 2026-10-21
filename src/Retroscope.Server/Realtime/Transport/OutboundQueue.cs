using System.Net.WebSockets;
using System.Threading.Channels;

namespace Retroscope.Server.Realtime.Transport;

/// <summary>
/// One connection's ordered outbound frames, bounded in bytes (R11). Bytes count from enqueue until the socket has
/// taken them, so a client that stops reading fills the queue even though the frames are small.
/// </summary>
internal sealed class OutboundQueue(long capacity) {
    private readonly Channel<Outbound> _channel = Channel.CreateUnbounded<Outbound>(new UnboundedChannelOptions {
        SingleReader = true,
    });

    private long _pending;

    public long PendingBytes => Interlocked.Read(ref _pending);

    public ChannelReader<Outbound> Reader => _channel.Reader;

    /// <summary>False when the frame would take the queue over capacity, or the queue is closed.</summary>
    public bool TryEnqueue(Outbound item) {
        if (Interlocked.Add(ref _pending, item.Length) > capacity || !_channel.Writer.TryWrite(item)) {
            Interlocked.Add(ref _pending, -item.Length);
            return false;
        }
        return true;
    }

    public void Sent(Outbound item) => Interlocked.Add(ref _pending, -item.Length);

    /// <summary>Queues the close frame after everything already queued and refuses anything later.</summary>
    public void Close(WebSocketCloseStatus status, string reason) {
        _channel.Writer.TryWrite(Outbound.Closing(status, reason));
        _channel.Writer.TryComplete();
    }

    public void Complete() => _channel.Writer.TryComplete();
}

/// <summary>One frame (one segment or several sent as fragments of a single message), or the close frame.</summary>
internal readonly struct Outbound {
    private Outbound(ReadOnlyMemory<byte> single, IReadOnlyList<ReadOnlyMemory<byte>>? segments, int length) {
        Single = single;
        Segments = segments;
        Length = length;
    }

    private Outbound(WebSocketCloseStatus status, string reason) {
        CloseStatus = status;
        CloseReason = reason;
    }

    public ReadOnlyMemory<byte> Single { get; }

    public IReadOnlyList<ReadOnlyMemory<byte>>? Segments { get; }

    public int Length { get; }

    public WebSocketCloseStatus? CloseStatus { get; }

    public string? CloseReason { get; }

    public static Outbound Frame(ReadOnlyMemory<byte> utf8) => new(utf8, null, utf8.Length);

    public static Outbound Frame(IReadOnlyList<ReadOnlyMemory<byte>> segments) {
        var length = 0;
        foreach (var segment in segments) {
            length += segment.Length;
        }
        return new(default, segments, length);
    }

    public static Outbound Closing(WebSocketCloseStatus status, string reason) => new(status, reason);
}
