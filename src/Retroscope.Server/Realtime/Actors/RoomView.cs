using Retroscope.Core.Ids;
using Retroscope.Core.Room;

namespace Retroscope.Server.Realtime.Actors;

/// <summary>
/// A room as a query sees it, on the actor's thread. <see cref="State"/> must only be read: the actor owns it.
/// </summary>
public sealed class RoomView<TState> where TState : class {
    private readonly Func<Viewer, bool> _canSubscribe;
    private readonly Func<Viewer, ValueTask<byte[]>> _snapFrame;

    internal RoomView(
        TState? state,
        Epoch epoch,
        long seq,
        PresenceView presence,
        Func<Viewer, bool> canSubscribe,
        Func<Viewer, ValueTask<byte[]>> snapFrame) {
        State = state;
        Epoch = epoch;
        Seq = seq;
        Presence = presence;
        _canSubscribe = canSubscribe;
        _snapFrame = snapFrame;
    }

    /// <summary>A room that does not exist (or was deleted): <see cref="Found"/> is false and nobody may subscribe.</summary>
    internal static RoomView<TState> NotFound(Epoch epoch, long seq, PresenceView presence) {
        return new RoomView<TState>(
            null,
            epoch,
            seq,
            presence,
            static _ => false,
            static _ => throw new InvalidOperationException("The room does not exist."));
    }

    /// <summary>False when the room does not exist (or was deleted).</summary>
    public bool Found => State is not null;

    public TState? State { get; }

    public Epoch Epoch { get; }

    public long Seq { get; }

    public PresenceView Presence { get; }

    public bool CanSubscribe(Viewer viewer) => Found && _canSubscribe(viewer);

    /// <summary>
    /// The viewer's <c>snap</c> frame at the current seq, for the boot snapshot (§c.3). <c>lastQ</c> is 0: the
    /// socket's replay after <c>hello</c> carries the real cursor.
    /// </summary>
    public ValueTask<byte[]> SnapFrameAsync(Viewer viewer) {
        return Found ? _snapFrame(viewer) : throw new InvalidOperationException("The room does not exist.");
    }
}
