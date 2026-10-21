using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;

namespace Retroscope.Server.Realtime.Actors;

/// <summary>A connection as an actor sees it: an ordered, byte-bounded outbound queue (U14 implements it).</summary>
public interface IRoomPeer {
    ConnectionId Id { get; }

    /// <summary>
    /// Queues a serialized server frame behind every frame queued before it. Returns false once the connection is
    /// closing, including when this frame overflowed its outbound budget (the connection then closes with 4008).
    /// </summary>
    bool Send(byte[] frame);

    /// <summary>Ends the subscription to <paramref name="room"/> with a <c>kick</c>; the connection forgets the room.</summary>
    bool Kick(RoomKey room, KickCode code) => Send(FrameBytes.Frame(new KickFrame(room, code)));

    /// <summary>Sends the <c>snap</c> or <c>replay</c> that starts (or resumes) the subscription's seq.</summary>
    bool SendSync(byte[] frame) => Send(frame);

    /// <summary>
    /// False once the connection has dropped this subscription (kicked, unsubscribed or closed) after it was queued;
    /// the actor then does not start it, so no subscription outlives the connection's own record of it.
    /// </summary>
    bool Wanted => true;

    /// <summary>Sends <c>bye{restart}</c> and closes with 1012 so the client reconnects and resends (R2, R3).</summary>
    void Restart();
}
