using System.Text.Json;
using Retroscope.Core.Ids;

namespace Retroscope.Core.Protocol;

// Frames (DESIGN §c.3). Field glossary: t type, r room, e epoch, s seq, q client command sequence, k kind,
// d data. WireParser reads them and WireWriter writes them; they are not STJ-polymorphic because the type
// of d depends on its sibling k (or, for snapshots, on the kind of r).

public abstract record ClientFrame;

public sealed record SubCursor(RoomKey R, Epoch? E = null, long? S = null);

public sealed record HelloFrame(int V, ClientId Cid, IReadOnlyList<SubCursor> Subs) : ClientFrame;

public sealed record SubFrame(RoomKey R, Epoch? E = null, long? S = null) : ClientFrame;

public sealed record UnsubFrame(RoomKey R) : ClientFrame;

public sealed record CmdFrame(RoomKey R, long Q, Command D) : ClientFrame {
    public string K => Catalog.KindOf(D);

    /// <summary>FNV-1a of the raw <c>d</c> bytes as received; 0 for a frame built in memory.</summary>
    public uint DataHash { get; init; }
}

public sealed record PingFrame(long C) : ClientFrame;

public abstract record ServerFrame;

public sealed record WelcomeFrame(ConnectionId Conn, PrincipalInfo Me, long N) : ServerFrame;

/// <param name="O">The per-viewer overlay; team rooms have none.</param>
public sealed record SnapFrame(RoomKey R, Epoch E, long S, long LastQ, RoomSnapshot D, RoomOverlay? O = null) : ServerFrame;

public sealed record TxRecord(long S, IReadOnlyList<WireEvent> Ev);

/// <param name="Txs">Empty means the client is current.</param>
public sealed record ReplayFrame(RoomKey R, Epoch E, long From, long LastQ, IReadOnlyList<TxRecord> Txs) : ServerFrame;

/// <param name="Ev">May be empty when every event was redacted for this viewer; the seq still advances.</param>
public sealed record TxFrame(RoomKey R, long S, IReadOnlyList<WireEvent> Ev) : ServerFrame;

public sealed record AckFrame(RoomKey R, long Q, long S, bool Dup = false) : ServerFrame;

/// <param name="D">Code-specific detail, e.g. <c>{"current":…}</c> for <see cref="NackCode.Stale"/>.</param>
public sealed record NackFrame(RoomKey R, long Q, NackCode Code, string? Msg = null, JsonElement? D = null) : ServerFrame {
    public bool Retry => Code.IsRetryable();
}

public sealed record XFrame(RoomKey R, Unsequenced D) : ServerFrame {
    public string K => Catalog.KindOf(D);
}

public sealed record PongFrame(long C, long N) : ServerFrame;

public sealed record KickFrame(RoomKey R, KickCode Code) : ServerFrame;

/// <param name="RetryMs">Drawn per connection so a restart does not cause a synchronized reconnect storm.</param>
public sealed record ByeFrame(ByeCode Code, int RetryMs) : ServerFrame;
