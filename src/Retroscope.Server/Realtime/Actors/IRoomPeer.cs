using Retroscope.Core.Ids;

namespace Retroscope.Server.Realtime.Actors;

/// <summary>A connection as an actor sees it: an ordered, byte-bounded outbound queue (U14 implements it).</summary>
public interface IRoomPeer {
    ConnectionId Id { get; }

    /// <summary>
    /// Queues a serialized server frame behind every frame queued before it. Returns false once the connection is
    /// closing, including when this frame overflowed its outbound budget (the connection then closes with 4008).
    /// </summary>
    bool Send(byte[] frame);

    /// <summary>Sends <c>bye{restart}</c> and closes with 1012 so the client reconnects and resends (R2, R3).</summary>
    void Restart();
}
