using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Core.Tests.Poker;
using Retroscope.Core.Tests.Retro;
using Retroscope.Server.Realtime.Actors;

namespace Retroscope.Sim;

/// <summary>An outbox entry (U28 <c>outbox.js</c>): the intent, the room and its current <c>q</c>.</summary>
internal sealed class Entry(Intent intent, RoomKey room, long q) {
    public Intent Intent { get; } = intent;

    public RoomKey Room { get; } = room;

    public long Q { get; set; } = q;

    /// <summary>Went out under its current <c>q</c> at least once.</summary>
    public bool Sent { get; set; }

    /// <summary>The connection it last went out on; a new connection resends it.</summary>
    public ConnectionId? SentOn { get; set; }

    /// <summary>
    /// Created by this page load before its room first synced (memory only), so its q may come from a <c>cseq</c> that
    /// storage had let fall behind the cursor. Only such entries are renumbered at sync: any other entry the cursor has
    /// passed was sent, by this tab, by an earlier load whose storage went back, or by a duplicate of this tab.
    /// </summary>
    public bool Early { get; init; }

    public Entry Copy() => new(Intent, Room, Q) { Sent = Sent };
}

/// <summary>What sessionStorage holds for a tab: cid, cseq and the outbox.</summary>
internal sealed record Storage(ClientId Cid, long Cseq, IReadOnlyList<Entry> Entries);

internal enum Reload : byte {
    /// <summary>A page reload with intact storage.</summary>
    Intact,

    /// <summary>The stored <c>cseq</c> fell behind the server's cursor, with the outbox stored next to it (R4).</summary>
    RegressedCseq,

    /// <summary>The outbox (and its <c>cseq</c>) is gone; the cid survived.</summary>
    LostOutbox,

    /// <summary>Nothing survived: a fresh cid.</summary>
    LostStorage,

    /// <summary>The whole of sessionStorage went back to an older copy (opt-in: see the U15 decisions).</summary>
    StaleStorage,
}

/// <summary>One room as a tab holds it (U28 <c>room.js</c>): confirmed state, sync flags and the retry timer.</summary>
internal sealed class RoomClient(RoomKey key) {
    public RoomKey Key { get; } = key;

    public Epoch? Epoch { get; set; }

    public long? Seq { get; set; }

    public bool Synced { get; set; }

    /// <summary>Synced at least once since the page loaded.</summary>
    public bool EverSynced { get; set; }

    public bool Resyncing { get; set; }

    public KickCode? Kicked { get; set; }

    public ViewerModel? Poker { get; set; }

    public List<PrincipalInfo> Presence { get; set; } = [];

    public TeamViewModel? Team { get; set; }

    /// <summary>The board's fold, built from a snapshot without its presence (<see cref="Presence"/> holds that).</summary>
    public RetroViewerModel? Retro { get; set; }

    /// <summary>The ends of the timers this page was told expired; each is told once (R16).</summary>
    public HashSet<long> TimerExpiries { get; } = [];

    public int RetryAttempt { get; set; }

    public ITimer? Retry { get; set; }

    public List<(long Q, bool Dup)> DeferredAcks { get; } = [];
}

/// <summary>
/// A browser tab following the client rules of §c.3, §g.4 and U28: the outbox resends with the same <c>(cid, q)</c>
/// after a reconnect, renumbers after a retryable nack or at sync, defers acks while resyncing, re-mints after
/// <c>cid_reuse</c> or 4409, and folds every frame into the same models the golden transcripts check.
/// </summary>
internal sealed class SimTab {
    private static readonly TimeSpan EphemeralMaxAge = TimeSpan.FromSeconds(10);

    private readonly World _world;
    private readonly List<Entry> _entries = [];
    private readonly Dictionary<RoomKey, RoomClient> _rooms = [];
    private long _cseq;
    private int _reconnectAttempt;
    private ITimer? _reconnect;
    private bool _restoredCid;

    public SimTab(World world, string name, Principal who, ClientId cid, IEnumerable<RoomKey> rooms) {
        _world = world;
        Name = name;
        Who = who;
        Cid = cid;
        foreach (var room in rooms) {
            _rooms[room] = new RoomClient(room);
        }
    }

    public string Name { get; }

    public Principal Who { get; }

    public ClientId Cid { get; private set; }

    public SimConnection? Connection { get; private set; }

    /// <summary>The tab was closed for good (its storage lives on in a duplicate, or nowhere).</summary>
    public bool Closed { get; private set; }

    public bool Reconnecting => _reconnect is not null;

    public IEnumerable<RoomClient> Rooms => _rooms.Values;

    public IReadOnlyList<Entry> Entries => _entries;

    public Viewer Viewer => new(Who, Cid, Connection?.Id ?? default);

    public RoomClient Room(RoomKey key) => _rooms[key];

    public Storage Save() => new(Cid, _cseq, [.. _entries.Select(x => x.Copy())]);

    public override string ToString() => Name;

    public async Task ConnectAsync() {
        if (Closed || Connection is { ClientClosed: false }) {
            return;
        }
        CancelReconnect();
        if (_restoredCid) {
            // U28 decision 7: a tab that restored its cid asks BroadcastChannel and waits 100 ms; a throttled
            // original may miss that window, and then only the server's 4409 tells them apart.
            _restoredCid = false;
            if (_world.TwinAnswers(this) && _world.Rng.Next(2) == 0) {
                Yield("a duplicated tab yields its cid and outbox to the tab it copied (boot check)");
            }
        }
        if (_world.CidHeldElsewhere(this)) {
            // 4409 cid_in_use: a twin that answers the check owns the cid and the outbox. Otherwise the holder is this
            // tab's own dead socket, which keep-alive reaps; the cid and every q are kept, so the cursor still dedupes.
            if (_world.TwinAnswers(this)) {
                Yield("a duplicated tab yields its cid and outbox to the tab it copied (4409)");
            } else {
                _world.Trace($"{Name}: 4409, keeping cid {Cid}");
            }
            ScheduleReconnect(Backoff());
            return;
        }
        var connection = _world.Open(this, new ConnectionId(_world.NewGuid()), Cid);
        Connection = connection;
        foreach (var room in _rooms.Values.Where(x => x.Kicked is null)) {
            await SubscribeAsync(room);
        }
    }

    public async Task SubscribeAsync(RoomClient room) {
        if (Connection is not { Open: true } connection) {
            return;
        }
        connection.Rooms.Add(room.Key);
        await _world.SendAsync(() => connection.Process.Gateway.Subscribe(connection, room.Key, room.Epoch, room.Seq));
    }

    /// <summary>A kicked room is joined again with a fresh subscription (the user reopened the page).</summary>
    public async Task RejoinAsync(RoomClient room) {
        room.Kicked = null;
        room.Epoch = null;
        room.Seq = null;
        await SubscribeAsync(room);
    }

    public async Task IssueAsync(RoomKey key, Command command) {
        var room = _rooms[key];
        if (room.Kicked is not null || Closed) {
            return;
        }
        var intent = _world.NewIntent(key, command, Who.Id);
        var entry = new Entry(intent, key, ++_cseq) { Early = !room.EverSynced };
        _entries.Add(entry);
        _world.Trace($"{Name}: issue {intent} q={entry.Q}");
        if (CanSend(room)) {
            await TransmitAsync(entry);
        }
    }

    public async Task HandleAsync(ServerFrame frame) {
        switch (frame) {
            case SnapFrame snap when _rooms.TryGetValue(snap.R, out var room):
                OnSnap(room, snap);
                await SettleAsync(room, snap.LastQ);
                break;
            case ReplayFrame replay when _rooms.TryGetValue(replay.R, out var room):
                await OnReplayAsync(room, replay);
                break;
            case TxFrame tx when _rooms.TryGetValue(tx.R, out var room):
                await OnTxAsync(room, tx);
                break;
            case AckFrame ack when _rooms.TryGetValue(ack.R, out var room):
                OnAck(room, ack.Q, ack.Dup);
                break;
            case NackFrame nack when _rooms.TryGetValue(nack.R, out var room):
                await OnNackAsync(room, nack);
                break;
            case KickFrame kick when _rooms.TryGetValue(kick.R, out var room):
                OnKick(room, kick.Code);
                break;
            case ByeFrame bye:
                _world.Trace($"{Name}: bye, reconnect in {bye.RetryMs} ms");
                Connection?.CloseClient();
                Disconnected();
                // U28 decision 5: reconnect after exactly retryMs.
                ScheduleReconnect(TimeSpan.FromMilliseconds(bye.RetryMs));
                break;
        }
    }

    /// <summary>The socket died without a bye (network drop, server crash): jittered backoff.</summary>
    public void Dropped() {
        Connection?.CloseClient();
        Disconnected();
        ScheduleReconnect(Backoff());
    }

    /// <summary>The tab closed its own socket (reload, re-mint): the server hears about it at once.</summary>
    private async Task CloseConnectionAsync() {
        if (Connection is { ClientClosed: false } connection) {
            connection.CloseClient();
            Disconnected();
            await _world.ServerNoticesCloseAsync(connection);
        }
    }

    /// <summary>A page reload; storage survives as <paramref name="kind"/> says.</summary>
    public async Task ReloadAsync(Reload kind, Storage? older) {
        await CloseConnectionAsync();
        CancelReconnect();
        foreach (var room in _rooms.Values) {
            StopRetry(room);
        }
        foreach (var key in _rooms.Keys.ToList()) {
            _rooms[key] = new RoomClient(key);
        }
        switch (kind) {
            case Reload.RegressedCseq:
                // U28 keeps cseq and the entries in one stored value, so they regress together: entries newer
                // than the older cseq are gone, the rest are as they were.
                _cseq = _world.Rng.NextInt64(0, _cseq + 1);
                foreach (var newer in _entries.Where(x => x.Q > _cseq).ToList()) {
                    _entries.Remove(newer);
                    _world.Oracle.Lost(newer.Intent, "sessionStorage regressed");
                }
                break;
            case Reload.LostOutbox:
                LoseAll("the outbox was lost");
                _cseq = 0;
                break;
            case Reload.LostStorage:
                LoseAll("sessionStorage was lost");
                _cseq = 0;
                Cid = new ClientId(_world.NewGuid());
                break;
            case Reload.StaleStorage when older is not null:
                var restored = older.Entries.Select(x => x.Intent).ToHashSet();
                foreach (var entry in _entries.Where(x => !restored.Contains(x.Intent))) {
                    _world.Oracle.Lost(entry.Intent, "sessionStorage went back to an older copy");
                }
                _entries.Clear();
                _entries.AddRange(older.Entries.Select(x => x.Copy()));
                foreach (var entry in _entries.Where(x => x.Intent.Outcome != IntentOutcome.Pending)) {
                    entry.Intent.Resurrected = true;
                }
                _cseq = older.Cseq;
                Cid = older.Cid;
                break;
        }
        for (var i = 0; i < _entries.Count; i++) {
            _entries[i] = _entries[i].Copy();
        }
        _restoredCid = kind != Reload.LostStorage;
        _world.Trace($"{Name}: reload ({kind}), cid {Cid}, cseq {_cseq}, {_entries.Count} entries");
        await ConnectAsync();
    }

    /// <summary>A duplicated tab takes over this tab's storage; when it is not a twin this tab closes for good.</summary>
    public SimTab Duplicate(string name, bool twin) {
        var copy = new SimTab(_world, name, Who, Cid, _rooms.Keys) { _cseq = _cseq, _restoredCid = true };
        copy._entries.AddRange(_entries.Select(x => x.Copy()));
        if (twin) {
            _world.Oracle.Twinned(Cid);
            foreach (var entry in _entries) {
                entry.Intent.Shared = true;
            }
        }
        return copy;
    }

    public async Task CloseForeverAsync() {
        Closed = true;
        CancelReconnect();
        foreach (var room in _rooms.Values) {
            StopRetry(room);
        }
        _entries.Clear();
        await CloseConnectionAsync();
    }

    private void OnSnap(RoomClient room, SnapFrame snap) {
        _world.Trace($"{Name}: snap {room.Key.Kind} e={snap.E} s={snap.S} lastQ={snap.LastQ}");
        room.Epoch = snap.E;
        room.Seq = snap.S;
        switch (snap.D) {
            case PokerSnapshot poker:
                _world.CheckSnapshotSecrecy(this, poker);
                room.Poker = new ViewerModel(poker, (PokerOverlay)snap.O!, Who.Id);
                room.Presence = [.. poker.Presence];
                break;
            case TeamSnapshot team:
                room.Team = new TeamViewModel(team);
                break;
            case RetroSnapshot retro:
                room.Retro = new RetroViewerModel(retro with { Presence = [] }, (RetroOverlay)snap.O!);
                room.Presence = [.. retro.Presence];
                break;
        }
    }

    private async Task OnReplayAsync(RoomClient room, ReplayFrame replay) {
        if (replay.E != room.Epoch || room.Seq is null) {
            await ResubscribeAsync(room);
            return;
        }
        if (replay.From != room.Seq + 1) {
            _world.Oracle.Fail($"{Name}: replay of {room.Key.Kind} starts at {replay.From}, expected {room.Seq + 1}");
        }
        foreach (var tx in replay.Txs) {
            if (tx.S <= room.Seq) {
                continue;
            }
            if (tx.S != room.Seq + 1) {
                _world.Oracle.Fail($"{Name}: replay of {room.Key.Kind} skips from {room.Seq} to {tx.S}");
                await ResubscribeAsync(room);
                return;
            }
            Apply(room, tx.S, tx.Ev);
        }
        await SettleAsync(room, replay.LastQ);
    }

    private async Task OnTxAsync(RoomClient room, TxFrame tx) {
        if (room.Resyncing || room.Seq is null || tx.S <= room.Seq) {
            return;
        }
        if (tx.S != room.Seq + 1) {
            _world.Oracle.Fail($"{Name}: tx {tx.S} of {room.Key.Kind} after {room.Seq} (gap in epoch {room.Epoch})");
            await ResubscribeAsync(room);
            return;
        }
        Apply(room, tx.S, tx.Ev);
    }

    private void Apply(RoomClient room, long seq, IReadOnlyList<WireEvent> events) {
        room.Seq = seq;
        if (room.Team is { } team) {
            foreach (var evt in events) {
                team.Apply(evt);
            }
            return;
        }
        var domain = new List<WireEvent>(events.Count);
        foreach (var evt in events) {
            switch (evt) {
                case PresenceJoin join:
                    room.Presence.RemoveAll(x => x.Pid == join.Pid);
                    room.Presence.Add(new PrincipalInfo(join.Pid, join.Kind, join.Name, join.Avatar));
                    break;
                case PresenceLeave leave:
                    room.Presence.RemoveAll(x => x.Pid == leave.Pid);
                    break;
                case PresenceUpdate update:
                    var index = room.Presence.FindIndex(x => x.Pid == update.Pid);
                    if (index >= 0) {
                        room.Presence[index] = room.Presence[index] with { Name = update.Name, Avatar = update.Avatar };
                    }
                    break;
                default:
                    domain.Add(evt);
                    break;
            }
        }
        room.Poker?.Apply(domain);
        if (room.Retro is { } retro) {
            foreach (var evt in domain) {
                if (evt is RetroTimer { Expired: true, EndsAt: { } end } && !room.TimerExpiries.Add(end)) {
                    _world.Oracle.Fail($"{Name}: the timer that ended at {end} was told expired twice");
                }
                retro.Apply(evt);
            }
        }
    }

    private async Task SettleAsync(RoomClient room, long lastQ) {
        _cseq = Math.Max(_cseq, lastQ);
        foreach (var entry in _entries.Where(x => x.Room == room.Key && x.Early && !x.Sent && x.Q <= lastQ)) {
            entry.Q = ++_cseq;
        }
        SortEntries();
        var cutoff = _world.Clock.Now - EphemeralMaxAge;
        foreach (var stale in _entries.Where(x => x.Room == room.Key && x.Intent.Ephemeral && x.Intent.IssuedAt < cutoff).ToList()) {
            _entries.Remove(stale);
            _world.Oracle.Dropped(stale.Intent, "ephemeral and older than 10 s");
        }
        foreach (var (q, dup) in room.DeferredAcks) {
            if (Find(room.Key, q) is { } entry) {
                _entries.Remove(entry);
                _world.Oracle.Acked(entry.Intent, dup, Cid, q);
            }
        }
        room.DeferredAcks.Clear();
        room.Synced = true;
        room.EverSynced = true;
        room.Resyncing = false;
        StopRetry(room);
        _reconnectAttempt = 0;
        await FlushAsync(room);
    }

    private async Task ResubscribeAsync(RoomClient room) {
        room.Resyncing = true;
        await SubscribeAsync(room);
    }

    private void OnAck(RoomClient room, long q, bool dup) {
        if (room.Resyncing) {
            room.DeferredAcks.Add((q, dup));
            return;
        }
        if (Find(room.Key, q) is not { } entry) {
            return;
        }
        _entries.Remove(entry);
        _world.Trace($"{Name}: ack q={q}{(dup ? " dup" : "")} {entry.Intent}");
        _world.Oracle.Acked(entry.Intent, dup, Cid, q);
        room.RetryAttempt = 0;
    }

    private async Task OnNackAsync(RoomClient room, NackFrame nack) {
        if (Find(room.Key, nack.Q) is not { } entry) {
            return;
        }
        _world.Trace($"{Name}: nack q={nack.Q} {nack.Code} {entry.Intent}");
        if (nack.Code == NackCode.CidReuse) {
            if (_world.TwinAnswers(this)) {
                Yield("a duplicated tab yields its cid and outbox to the tab it copied (cid_reuse)");
                await CloseConnectionAsync();
                await ConnectAsync();
                return;
            }
            // Another intent of this cid holds that q (storage went back to an older copy); this one was not applied,
            // so it goes out again under a fresh q. Entries in flight behind it keep theirs: they may have committed.
            _world.Trace($"{Name}: cid_reuse on q={nack.Q}, renumbering it");
            entry.Q = ++_cseq;
            entry.Sent = false;
            entry.SentOn = null;
            SortEntries();
            ScheduleRetry(room);
            return;
        }
        if (nack.Code == NackCode.Forbidden && _world.Authored(entry.Intent.Command, Who.Id)) {
            // The board recognises its author by the anonymous tag too, across reloads and restarts.
            _world.Oracle.Fail($"{Name}: {entry.Intent} on its own post was refused as forbidden");
        }
        if (nack.Retry) {
            // The server refuses every later command of the room on this connection until it is subscribed again, so
            // the entry keeps its q: nothing behind it can commit first and turn the resend into a dup.
            entry.SentOn = null;
            ScheduleRetry(room);
            return;
        }
        _entries.Remove(entry);
        _world.Oracle.Nacked(entry.Intent, nack.Code);
    }

    private void OnKick(RoomClient room, KickCode code) {
        _world.Trace($"{Name}: kicked from {room.Key.Kind} ({code})");
        room.Kicked = code;
        room.Synced = false;
        StopRetry(room);
        foreach (var entry in _entries.Where(x => x.Room == room.Key).ToList()) {
            _entries.Remove(entry);
            _world.Oracle.Dropped(entry.Intent, $"kicked ({code})");
        }
        Connection?.Rooms.Remove(room.Key);
    }

    private bool CanSend(RoomClient room) {
        return room is { Synced: true, Resyncing: false, Kicked: null, Retry: null } && Connection is { ClientClosed: false };
    }

    private async Task FlushAsync(RoomClient room) {
        if (!CanSend(room)) {
            return;
        }
        var connection = Connection!.Id;
        foreach (var entry in _entries.Where(x => x.Room == room.Key).ToList()) {
            if (entry.SentOn != connection && _entries.Contains(entry) && CanSend(room)) {
                await TransmitAsync(entry);
            }
        }
    }

    private async Task TransmitAsync(Entry entry) {
        var connection = Connection!;
        entry.Sent = true;
        entry.SentOn = connection.Id;
        _world.Oracle.Transmitted(entry.Intent, Cid, entry.Q);
        _world.Trace($"{Name}: send q={entry.Q} {entry.Intent}{(connection.Open ? "" : " (lost: socket closing)")}");
        if (!connection.Open) {
            // The socket is closing under us: the frame is written and lost, as a browser's send would be.
            return;
        }
        var frame = new CmdFrame(entry.Room, entry.Q, entry.Intent.Command) { DataHash = entry.Intent.DataHash };
        await _world.SendAsync(() => connection.Process.Gateway.Command(connection, frame));
    }

    private void ScheduleRetry(RoomClient room) {
        StopRetry(room);
        var delay = TimeSpan.FromMilliseconds(Math.Min(5000, 250 * Math.Pow(2, room.RetryAttempt++)));
        // The retry goes out after a fresh sub, which lifts the server's refusal of the room's later commands.
        room.Retry = _world.Clock.Schedule(_world.Clock.Now + delay, async () => {
            room.Retry = null;
            await ResubscribeAsync(room);
        });
    }

    private static void StopRetry(RoomClient room) {
        room.Retry?.Dispose();
        room.Retry = null;
    }

    private void Disconnected() {
        foreach (var room in _rooms.Values) {
            room.Synced = false;
            room.Resyncing = false;
            StopRetry(room);
        }
    }

    /// <summary>U28 decision 11: full jitter over <c>[0, min(10 s, 250 ms × 2ⁿ))</c>.</summary>
    private TimeSpan Backoff() {
        var cap = Math.Min(10_000, 250 * Math.Pow(2, _reconnectAttempt++));
        return TimeSpan.FromMilliseconds(_world.Rng.NextDouble() * cap);
    }

    private void ScheduleReconnect(TimeSpan delay) {
        CancelReconnect();
        _reconnect = _world.Clock.Schedule(_world.Clock.Now + delay, async () => {
            _reconnect = null;
            await ConnectAsync();
        });
    }

    private void CancelReconnect() {
        _reconnect?.Dispose();
        _reconnect = null;
    }

    /// <summary>A live twin answered with this cid: like a duplicate at boot, mint a new cid and start empty (§g.4).</summary>
    private void Yield(string why) {
        _world.Trace($"{Name}: {why}");
        LoseAll(why);
        Cid = new ClientId(_world.NewGuid());
    }

    private void LoseAll(string why) {
        foreach (var entry in _entries) {
            _world.Oracle.Lost(entry.Intent, why);
        }
        _entries.Clear();
    }

    private Entry? Find(RoomKey room, long q) => _entries.Find(x => x.Room == room && x.Q == q);

    private void SortEntries() => _entries.Sort((x, y) => x.Q.CompareTo(y.Q));
}
