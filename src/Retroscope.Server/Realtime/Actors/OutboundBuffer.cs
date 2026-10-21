using System.Threading.Channels;
using Retroscope.Core.Protocol;

namespace Retroscope.Server.Realtime.Actors;

/// <summary>
/// The frames waiting to be written to one socket, bounded in bytes (§c.7: 1 MB). A frame that would overflow the
/// budget closes the buffer with 4008 instead of growing it, so a slow consumer cannot hold memory hostage; the
/// client resumes with <c>sub {e, s}</c> after reconnecting.
/// </summary>
public sealed class OutboundBuffer(long capacityBytes = OutboundBuffer.DefaultCapacityBytes) {
    public const long DefaultCapacityBytes = 1024 * 1024;

    private readonly Channel<byte[]> _frames = Channel.CreateUnbounded<byte[]>(new UnboundedChannelOptions {
        SingleReader = true,
    });
    private readonly TaskCompletionSource<int> _closed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long _pending;

    public long PendingBytes => Interlocked.Read(ref _pending);

    /// <summary>Completes with the close code once the buffer is closed.</summary>
    public Task<int> Closed => _closed.Task;

    public bool TryEnqueue(byte[] frame) {
        if (_closed.Task.IsCompleted) {
            return false;
        }
        if (Interlocked.Add(ref _pending, frame.Length) > capacityBytes) {
            Interlocked.Add(ref _pending, -frame.Length);
            Close(CloseCodes.SlowConsumer);
            return false;
        }
        if (!_frames.Writer.TryWrite(frame)) {
            Interlocked.Add(ref _pending, -frame.Length);
            return false;
        }
        return true;
    }

    /// <summary>The next frame to write, or null once the buffer is closed (pending frames are dropped then).</summary>
    public async ValueTask<byte[]?> DequeueAsync(CancellationToken cancellationToken) {
        while (await _frames.Reader.WaitToReadAsync(cancellationToken)) {
            if (_closed.Task.IsCompleted) {
                return null;
            }
            if (_frames.Reader.TryRead(out var frame)) {
                Interlocked.Add(ref _pending, -frame.Length);
                return frame;
            }
        }
        return null;
    }

    /// <summary>Closes the buffer; the first close code wins.</summary>
    public bool Close(int closeCode) {
        if (!_closed.TrySetResult(closeCode)) {
            return false;
        }
        _frames.Writer.TryComplete();
        return true;
    }
}
