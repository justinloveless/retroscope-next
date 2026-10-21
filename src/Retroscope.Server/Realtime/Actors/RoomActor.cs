using System.Threading.Channels;
using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Server.Data;
using Retroscope.Server.Data.Repos;

namespace Retroscope.Server.Realtime.Actors;

/// <summary>A message on an actor's queues; only the runtime creates them.</summary>
public abstract record ActorMessage {
    private protected ActorMessage() {
    }
}

/// <summary>
/// The single writer of one room (§c.7). Two queues: an unbounded internal one (resumes, connection closes, posts,
/// asks, queries, timers) that is always drained first, and a bounded external one (client commands and new
/// subscriptions) written with <c>TryWrite</c>, so a full room answers <c>server_busy</c> instead of blocking a
/// socket (R10, R26). Every client command runs: dedupe → decide → one fenced transaction → apply → fan out → ack →
/// reschedule timers. A failure whose outcome is unknown poisons the actor: every subscriber gets <c>bye</c> and
/// 1012, and the next message reloads the room from the database with a new epoch (R2).
/// </summary>
public sealed class RoomActor<TState, TInput, TEvent> : IRoomActor<TState, TInput>, IClosable
    where TState : class
    where TInput : class {
    private readonly Channel<ActorMessage> _internal = Channel.CreateUnbounded<ActorMessage>(new UnboundedChannelOptions {
        SingleReader = true,
    });
    private readonly Channel<ActorMessage> _external;
    private readonly IRoomBinding<TState, TInput, TEvent> _binding;
    private readonly IRoomStore<TState, TEvent> _store;
    private readonly IAccessView _access;
    private readonly ActorRuntime _runtime;
    private readonly ActorRegistry? _registry;
    private readonly ILogger _logger;
    private readonly Dictionary<ConnectionId, Subscriber> _subs = [];
    private readonly List<Action> _replies = [];
    private readonly PresenceTable _presence = new();
    private readonly LruCache<ClientId, CachedCursor> _cursors;
    private readonly LruCache<(ClientId Client, long Q), uint> _recentHashes;
    private readonly ActorTimers<string, TInput> _timers;
    private readonly ActorTimers<RuntimeTimer, long> _runtimeTimers;
    private readonly ByteBoundedRing _ring;
    private readonly byte[]?[] _shared = new byte[2][];
    private long _sharedSeq = -1;
    private TState? _state;
    private Status _status;
    private Epoch _epoch;
    private long _seq;
    private IReadOnlyList<TEvent>? _lastTx;
    private long _lastTxSeq;
    private volatile bool _closed;
    private Task<bool>? _internalReady;
    private Task<bool>? _externalReady;

    public RoomActor(
        RoomKey key,
        IRoomBinding<TState, TInput, TEvent> binding,
        IRoomStore<TState, TEvent> store,
        IAccessView access,
        ActorRuntime runtime,
        ActorRegistry? registry = null,
        IReadOnlyList<ActorMessage>? backlog = null) {
        if (key.Kind != binding.Kind) {
            throw new ArgumentException($"A {binding.Kind} binding cannot run room {key}.", nameof(key));
        }
        Key = key;
        _binding = binding;
        _store = store;
        _access = access;
        _runtime = runtime;
        _registry = registry;
        _logger = runtime.Loggers.CreateLogger("Retroscope.Server.Realtime.Actors.RoomActor");
        var options = runtime.Options;
        _external = Channel.CreateBounded<ActorMessage>(new BoundedChannelOptions(options.ExternalQueueCapacity) {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait,
        });
        _cursors = new(options.CursorCacheSize, runtime.Time, options.CursorTtl);
        _recentHashes = new(options.RecentHashes, runtime.Time);
        _timers = new(runtime.Time, (key, generation) => _internal.Writer.TryWrite(new DomainTimerFired(key, generation)));
        _runtimeTimers = new(runtime.Time, (key, generation) => _internal.Writer.TryWrite(new RuntimeTimerFired(key, generation)));
        _ring = new ByteBoundedRing(options.RingBytesPerRoom, runtime.RingBudget);
        foreach (var message in backlog ?? []) {
            _internal.Writer.TryWrite(message);
        }
        Completion = Task.Run(RunAsync);
    }

    private enum Status : byte {
        Unloaded,
        Loaded,
        Missing,
        Stopped,
    }

    public RoomKey Key { get; }

    /// <summary>Completes once the actor retired (evicted or purged).</summary>
    public Task Completion { get; }

    private DateTimeOffset Now => _runtime.Time.GetUtcNow();

    public PostResult Subscribe(IRoomPeer peer, Viewer viewer, Epoch? epoch = null, long? seq = null) {
        var message = new SubscribeMessage(peer, viewer, epoch, seq);
        return epoch is null ? Enqueue(_external, message) : Enqueue(_internal, message);
    }

    public PostResult Command(IRoomPeer peer, Viewer viewer, CmdFrame frame) {
        var result = Enqueue(_external, new CommandMessage(peer, viewer, frame));
        if (result == PostResult.Busy) {
            peer.Send(Nack(frame.Q, NackCode.ServerBusy));
        }
        return result;
    }

    public PostResult Unsubscribe(ConnectionId connection) => Enqueue(_internal, new LeaveMessage(connection));

    public PostResult ConnectionClosed(ConnectionId connection) => Enqueue(_internal, new LeaveMessage(connection));

    public PostResult AccessChanged() => Enqueue(_internal, new AccessChangedMessage());

    public PostResult PrincipalUpdated(PrincipalInfo principal) => Enqueue(_internal, new PrincipalUpdatedMessage(principal));

    public PostResult Post(TInput input) => Enqueue(_internal, new PostMessage(input));

    public async Task<AskResult> AskAsync(TInput input, Viewer? viewer, CancellationToken cancellationToken) {
        var reply = new TaskCompletionSource<AskResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Enqueue(_internal, new AskMessage(input, viewer, reply)) != PostResult.Accepted) {
            throw new ActorClosedException();
        }
        return await reply.Task.WaitAsync(cancellationToken);
    }

    public async Task<T> QueryAsync<T>(Func<RoomView<TState>, ValueTask<T>> query, CancellationToken cancellationToken) {
        var message = new QueryMessage<T>(query);
        if (Enqueue(_internal, message) != PostResult.Accepted) {
            throw new ActorClosedException();
        }
        return await message.Reply.Task.WaitAsync(cancellationToken);
    }

    /// <summary>Retires the actor if nobody is subscribed or waiting; false otherwise (retention purge, R18).</summary>
    async Task<bool> IClosable.TryCloseForPurgeAsync() {
        var reply = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (Enqueue(_internal, new PurgeMessage(reply)) != PostResult.Accepted) {
            return true;
        }
        return await reply.Task;
    }

    /// <summary>Graceful shutdown (§c.9): finish the current message, run the binding's last input, send <c>bye</c>.</summary>
    Task IClosable.ShutdownAsync() {
        var reply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        return Enqueue(_internal, new ShutdownMessage(reply)) == PostResult.Accepted ? reply.Task : Task.CompletedTask;
    }

    private PostResult Enqueue(Channel<ActorMessage> channel, ActorMessage message) {
        if (channel.Writer.TryWrite(message)) {
            return PostResult.Accepted;
        }
        return _closed ? PostResult.Closed : PostResult.Busy;
    }

    private async Task RunAsync() {
        while (await NextAsync() is { } message) {
            try {
                await HandleAsync(message);
                if (!_closed) {
                    AfterMessage();
                }
            } catch (Exception e) {
                ActorLog.HandlerFailed(_logger, e, Key, message.GetType().Name);
                Poison(message.GetType().Name, "handler failed", e);
                Abandon(message);
                if (!_closed) {
                    AfterMessage();
                }
            }
            // Asks and queries complete only once their timers are rescheduled, so a caller sees the whole effect.
            foreach (var reply in _replies) {
                reply();
            }
            _replies.Clear();
        }
    }

    /// <summary>The next message, internal before external; null once both queues are completed and drained.</summary>
    private async ValueTask<ActorMessage?> NextAsync() {
        while (true) {
            if (_internal.Reader.TryRead(out var message) || _external.Reader.TryRead(out message)) {
                return message;
            }
            // A pending wait is kept until it completes, so a quiet queue never collects abandoned waiters.
            var internalReady = _internalReady ??= _internal.Reader.WaitToReadAsync().AsTask();
            var externalReady = _externalReady ??= _external.Reader.WaitToReadAsync().AsTask();
            await Task.WhenAny(internalReady, externalReady);
            if (internalReady.IsCompleted) {
                _internalReady = null;
            }
            if (externalReady.IsCompleted) {
                _externalReady = null;
            }
            if (_internal.Reader.Completion.IsCompleted && _external.Reader.Completion.IsCompleted) {
                return null;
            }
        }
    }

    private async Task HandleAsync(ActorMessage message) {
        switch (message) {
            case SubscribeMessage subscribe:
                if (await ReadyAsync(message)) {
                    await SubscribeAsync(subscribe);
                }
                break;
            case CommandMessage command:
                if (await ReadyAsync(message)) {
                    await CommandAsync(command);
                }
                break;
            case PostMessage post:
                if (await ReadyAsync(message) && _status == Status.Loaded) {
                    await DecideInternalAsync(post.Input, null);
                }
                break;
            case AskMessage ask:
                if (await ReadyAsync(message)) {
                    var result = await AskAsync(ask);
                    _replies.Add(() => ask.Reply.TrySetResult(result));
                }
                break;
            case QueryMessage query:
                if (await ReadyAsync(message)) {
                    _replies.Add(await query.RunAsync(View()));
                }
                break;
            case LeaveMessage leave:
                Leave(leave.Connection);
                break;
            case AccessChangedMessage when _status == Status.Loaded:
                await RecheckAccessAsync();
                break;
            case PrincipalUpdatedMessage updated when _status == Status.Loaded:
                if (_presence.Update(updated.Principal) is { } delta) {
                    var info = updated.Principal;
                    await PresenceChangedAsync(delta, [new PresenceUpdate(info.Pid, info.Name, info.Avatar)]);
                }
                break;
            case DomainTimerFired fired when _status == Status.Loaded:
                if (_timers.TryTake(fired.Key, fired.Generation, out var input)) {
                    await DecideInternalAsync(input, null);
                }
                break;
            case RuntimeTimerFired fired:
                if (_runtimeTimers.TryTake(fired.Key, fired.Generation, out var payload)) {
                    await RuntimeTimerAsync(fired.Key, payload);
                }
                break;
            case PurgeMessage purge:
                // Anything still queued (a resume, an ask) would load the room again once the purge has run.
                if (_subs.Count > 0 || _internal.Reader.TryPeek(out _) || _external.Reader.Count > 0) {
                    purge.Reply.TrySetResult(false);
                } else {
                    Retire();
                    purge.Reply.TrySetResult(true);
                }
                break;
            case ShutdownMessage shutdown:
                await ShutdownAsync(shutdown);
                break;
        }
    }

    /// <summary>Loads the room when needed; false (and the message abandoned) when it cannot be loaded or has stopped.</summary>
    private async ValueTask<bool> ReadyAsync(ActorMessage message) {
        if (_status == Status.Unloaded) {
            await LoadAsync();
        }
        if (_status is Status.Loaded or Status.Missing) {
            return true;
        }
        Abandon(message);
        return false;
    }

    private async Task LoadAsync() {
        TState? state;
        var now = Now;
        try {
            state = await _store.LoadAsync(Key, now, CancellationToken.None);
        } catch (Exception e) {
            ActorLog.LoadFailed(_logger, e, Key);
            return;
        }
        _epoch = Epoch.NewRandom();
        _seq = 0;
        _ring.Reset(0);
        _sharedSeq = -1;
        _lastTx = null;
        if (state is null) {
            _status = Status.Missing;
            return;
        }
        _state = state;
        _status = Status.Loaded;
        if (_binding.WarmUntil(state) is { } warmUntil && warmUntil > now) {
            _runtimeTimers.Arm(RuntimeTimer.Warm, warmUntil, 0);
        } else {
            await WarmAsync();
        }
    }

    private async Task WarmAsync() {
        var events = _binding.Domain.OnWarm(_state!, _presence.View, Now);
        await CommitAndPublishAsync(events, [], null, "warm");
    }

    private async Task SubscribeAsync(SubscribeMessage message) {
        var (peer, viewer, epoch, seq) = message;
        if (_status == Status.Missing || _binding.IsDeleted(_state!)) {
            peer.Send(Kick(KickCode.NotFound));
            return;
        }
        var state = _state!;
        if (!_binding.CanSubscribe(state, viewer, _access)) {
            Leave(peer.Id);
            peer.Send(Kick(KickCode.Forbidden));
            return;
        }
        var existing = _subs.GetValueOrDefault(peer.Id);
        if (existing is null && _presence.ConnectionsOf(viewer.Who.Id) >= _runtime.Options.ConnectionsPerPrincipal) {
            peer.Send(Kick(KickCode.Forbidden));
            return;
        }
        var member = _binding.IsMember(state, viewer, _access);
        CachedCursor cursor;
        try {
            cursor = await CursorAsync(viewer);
        } catch (Exception e) {
            ActorLog.Poisoned(_logger, e, Key, "sub", "the cursor could not be loaded");
            peer.Restart();
            return;
        }
        await ResumeAsync(peer, viewer, member, epoch, seq, cursor.LastQ);
        _subs[peer.Id] = new Subscriber(peer, viewer, member);
        var delta = _presence.Add(peer.Id, viewer, member);
        if (existing is null) {
            var info = PresenceTable.Info(viewer.Who);
            IReadOnlyList<WireEvent> wire = delta.Change == PresenceChange.Joined
                ? [new PresenceJoin(info.Pid, info.Kind, info.Name, info.Avatar)]
                : [];
            await PresenceChangedAsync(delta, wire);
        }
    }

    /// <summary>§c.3: same epoch and seq → empty replay; same epoch and seq still in the ring → replay; else snapshot.</summary>
    private async Task ResumeAsync(IRoomPeer peer, Viewer viewer, bool member, Epoch? epoch, long? seq, long lastQ) {
        if (epoch == _epoch && seq is { } last && last <= _seq) {
            var txs = new List<(long Seq, byte[] Events)>();
            if (last == _seq || (last >= _ring.FirstSeq - 1 && _ring.TryReplay(last, _seq, new Audience(viewer.Who.Id, member), txs))) {
                peer.Send(FrameBytes.Replay(Key, _epoch, last + 1, lastQ, txs));
                return;
            }
        }
        peer.Send(await SnapFrameAsync(_state!, viewer, member, lastQ));
    }

    private async ValueTask<byte[]> SnapFrameAsync(TState state, Viewer viewer, bool member, long lastQ) {
        var shared = await SharedSnapshotAsync(state, member);
        return FrameBytes.Snap(Key, _epoch, _seq, lastQ, shared, _binding.Overlay(state, viewer));
    }

    /// <summary>The public document for this seq and variant, serialized once and shared by every viewer (R11).</summary>
    private async ValueTask<byte[]> SharedSnapshotAsync(TState state, bool member) {
        if (_sharedSeq != _seq) {
            Array.Clear(_shared);
            _sharedSeq = _seq;
        }
        var index = member ? 1 : 0;
        if (_shared[index] is { } cached) {
            return cached;
        }
        await _runtime.SnapshotGate.WaitAsync();
        try {
            var snapshot = _binding.Snapshot(state, _binding.TracksPresence ? _presence.Infos : [], member);
            return _shared[index] = FrameBytes.SharedSnapshot(Key.Kind, snapshot);
        } finally {
            _runtime.SnapshotGate.Release();
        }
    }

    private async Task CommandAsync(CommandMessage message) {
        var (peer, viewer, frame) = message;
        if (_status == Status.Missing) {
            peer.Send(Nack(frame.Q, NackCode.NotFound));
            return;
        }
        if (!_subs.ContainsKey(peer.Id)) {
            peer.Send(Nack(frame.Q, NackCode.NotSubscribed));
            return;
        }
        if (_binding.FromClient(frame.D) is not { } input) {
            peer.Send(Nack(frame.Q, NackCode.Invalid));
            return;
        }
        CachedCursor cursor;
        try {
            cursor = await CursorAsync(viewer);
        } catch (Exception e) {
            ActorLog.Poisoned(_logger, e, Key, frame.K, "the cursor could not be loaded");
            peer.Restart();
            return;
        }
        if (cursor.Owner == CursorOwner.OtherPrincipal) {
            peer.Send(Nack(frame.Q, NackCode.Forbidden));
            return;
        }
        if (frame.Q <= cursor.LastQ) {
            var reused = _recentHashes.TryGet((viewer.Client, frame.Q), out var hash) && hash != frame.DataHash;
            peer.Send(reused ? Nack(frame.Q, NackCode.CidReuse) : Ack(frame.Q, dup: true));
            return;
        }
        switch (_binding.Domain.Decide(_state!, input, Context(viewer))) {
            case Decision<TEvent>.Reject reject:
                peer.Send(FrameBytes.Frame(new NackFrame(Key, frame.Q, reject.Code, reject.Detail, reject.Data)));
                break;
            case Decision<TEvent>.NoOp:
                peer.Send(Ack(frame.Q));
                break;
            case Decision<TEvent>.Accept accept:
                var advance = new CursorAdvance(viewer.Client, viewer.Who.Id, frame.Q);
                switch (await CommitAndPublishAsync(accept.Events, [], advance, frame.K)) {
                    case Outcome.Applied:
                        _recentHashes.Set((viewer.Client, frame.Q), frame.DataHash);
                        peer.Send(Ack(frame.Q));
                        break;
                    case Outcome.Rejected:
                        peer.Send(Nack(frame.Q, NackCode.Internal));
                        break;
                }
                break;
        }
    }

    private async Task<AskResult> AskAsync(AskMessage ask) {
        if (_status == Status.Missing) {
            return new AskResult(AskStatus.NotFound);
        }
        switch (_binding.Domain.Decide(_state!, ask.Input, Context(ask.Viewer))) {
            case Decision<TEvent>.Reject reject:
                return new AskResult(AskStatus.Rejected, _seq, reject.Code, reject.Detail, reject.Data);
            case Decision<TEvent>.Accept accept:
                return await CommitAndPublishAsync(accept.Events, [], null, ask.Input.GetType().Name) switch {
                    Outcome.Applied => new AskResult(AskStatus.Accepted, _seq),
                    Outcome.Rejected => new AskResult(AskStatus.Rejected, _seq, NackCode.Internal),
                    _ => new AskResult(AskStatus.Unavailable),
                };
            default:
                return new AskResult(AskStatus.NoOp, _seq);
        }
    }

    private async Task DecideInternalAsync(TInput input, Viewer? viewer) {
        switch (_binding.Domain.Decide(_state!, input, Context(viewer))) {
            case Decision<TEvent>.Accept accept:
                await CommitAndPublishAsync(accept.Events, [], null, input.GetType().Name);
                break;
            case Decision<TEvent>.Reject reject:
                ActorLog.InternalRejected(_logger, Key, input.GetType().Name, reject.Code.ToString());
                break;
        }
    }

    /// <summary>
    /// Steps 3–5 of the pipeline: commit the committed events (and the cursor) in one fenced transaction, apply
    /// everything, and fan out a tx frame unless every event is memory-only. A decision without committed events
    /// needs no transaction and writes no cursor.
    /// </summary>
    private async Task<Outcome> CommitAndPublishAsync(
        IReadOnlyList<TEvent> events,
        IReadOnlyList<WireEvent> prefix,
        CursorAdvance? cursor,
        string kind) {
        var committed = events.Where(x => _binding.DurabilityOf(x) == EventDurability.Committed).ToList();
        if (committed.Count > 0) {
            try {
                await _store.CommitAsync(Key, _state!, committed, cursor, CancellationToken.None);
            } catch (CommitRejectedException e) {
                ActorLog.CommitRejected(_logger, e, Key, kind, e.SqlState);
                return Outcome.Rejected;
            } catch (FencingLostException e) {
                Poison(kind, "fencing lost", e);
                return Outcome.Failed;
            } catch (Exception e) {
                Poison(kind, "the commit failed or its outcome is unknown", e);
                return Outcome.Failed;
            }
            if (cursor is { } advance) {
                _cursors.Set(advance.Client, new CachedCursor(advance.Principal, CursorOwner.Caller, advance.Q));
            }
        }
        foreach (var evt in events) {
            _binding.Domain.Apply(_state!, evt);
        }
        var visible = events.Where(x => _binding.DurabilityOf(x) != EventDurability.MemoryOnly).ToList();
        if (visible.Count > 0 || prefix.Count > 0) {
            Publish(visible, prefix);
        }
        if (_binding.IsDeleted(_state!)) {
            KickAll(KickCode.Deleted);
        }
        return Outcome.Applied;
    }

    /// <summary>Sequences one tx, records it in the ring per audience and sends each subscriber its projection.</summary>
    private void Publish(IReadOnlyList<TEvent> events, IReadOnlyList<WireEvent> prefix) {
        _seq++;
        var byAudience = new Dictionary<Audience, byte[]>();
        var distinct = new Dictionary<byte[], byte[]>(ByteContentComparer.Instance);
        var audiences = _subs.Values.Select(x => x.Audience).Concat(_presence.Audiences);
        foreach (var audience in audiences) {
            if (byAudience.ContainsKey(audience)) {
                continue;
            }
            var projected = _binding.Project(_state!, events, audience.Principal, audience.Member);
            var bytes = FrameBytes.Events(prefix.Count == 0 ? projected : [.. prefix, .. projected]);
            if (!distinct.TryGetValue(bytes, out var shared)) {
                distinct[bytes] = shared = bytes;
            }
            byAudience[audience] = shared;
        }
        if (prefix.Count == 0 && _lastTx is { } previous && _lastTxSeq == _seq - 1 && _binding.Supersedes(events, previous)) {
            _ring.Blank(_seq - 1);
        }
        _ring.Append(_seq, byAudience);
        _lastTx = prefix.Count == 0 ? events : null;
        _lastTxSeq = _seq;
        var frames = new Dictionary<byte[], byte[]>(ReferenceEqualityComparer.Instance);
        foreach (var subscriber in _subs.Values) {
            var projection = byAudience[subscriber.Audience];
            if (!frames.TryGetValue(projection, out var frame)) {
                frames[projection] = frame = FrameBytes.Tx(Key, _seq, projection);
            }
            subscriber.Peer.Send(frame);
        }
    }

    private async Task PresenceChangedAsync(PresenceDelta delta, IReadOnlyList<WireEvent> wire) {
        var events = _binding.Domain.OnPresence(_state!, delta, _presence.View, Now);
        await CommitAndPublishAsync(events, _binding.TracksPresence ? wire : [], null, $"presence.{delta.Change}");
    }

    private void Leave(ConnectionId connection) {
        if (!_subs.Remove(connection, out var subscriber)) {
            return;
        }
        var principal = subscriber.Viewer.Who.Id;
        var grace = _presence.Remove(connection, principal);
        var dueAt = Now + _runtime.Options.PresenceGrace;
        if (grace.Leave is { } leave) {
            _runtimeTimers.Arm(new RuntimeTimer.Leave(principal), dueAt, leave);
        }
        if (grace.ClientGone is { } gone) {
            _runtimeTimers.Arm(new RuntimeTimer.ClientGrace(principal, grace.Client), dueAt, gone);
        }
    }

    private async Task RuntimeTimerAsync(RuntimeTimer key, long payload) {
        switch (key) {
            case RuntimeTimer.WarmTimer when _status == Status.Loaded:
                await WarmAsync();
                break;
            case RuntimeTimer.Leave leave when _status == Status.Loaded:
                if (_presence.ExpireLeave(leave.Principal, payload) is { } left) {
                    await PresenceChangedAsync(left, [new PresenceLeave(leave.Principal)]);
                }
                break;
            case RuntimeTimer.ClientGrace grace when _status == Status.Loaded:
                if (_presence.ExpireClient(grace.Principal, grace.Client, payload) is { } gone) {
                    await PresenceChangedAsync(gone, []);
                }
                break;
            case RuntimeTimer.IdleTimer:
                if (IsIdle() && _external.Reader.Count == 0 && !_internal.Reader.TryPeek(out _)) {
                    Retire();
                }
                break;
        }
    }

    private async Task RecheckAccessAsync() {
        foreach (var subscriber in _subs.Values.ToList()) {
            var viewer = subscriber.Viewer;
            if (!_binding.CanSubscribe(_state!, viewer, _access)) {
                subscriber.Peer.Send(Kick(KickCode.AccessRevoked));
                Leave(subscriber.Peer.Id);
                continue;
            }
            var member = _binding.IsMember(_state!, viewer, _access);
            if (member != subscriber.Member) {
                _subs[subscriber.Peer.Id] = subscriber with { Member = member };
                _presence.Add(subscriber.Peer.Id, viewer, member);
                CachedCursor cursor;
                try {
                    cursor = await CursorAsync(viewer);
                } catch (Exception e) {
                    ActorLog.Poisoned(_logger, e, Key, "access", "the cursor could not be loaded");
                    Leave(subscriber.Peer.Id);
                    subscriber.Peer.Restart();
                    continue;
                }
                subscriber.Peer.Send(await SnapFrameAsync(_state!, viewer, member, cursor.LastQ));
            }
        }
    }

    private async Task ShutdownAsync(ShutdownMessage message) {
        try {
            if (_status == Status.Loaded && !_binding.IsDeleted(_state!) && _binding.OnShutdown(_state!) is { } last) {
                await DecideInternalAsync(last, null);
            }
        } finally {
            foreach (var subscriber in _subs.Values) {
                subscriber.Peer.Restart();
            }
            _subs.Clear();
            _presence.Clear();
            _timers.Clear();
            _runtimeTimers.Clear();
            _status = Status.Stopped;
            message.Reply.TrySetResult();
        }
    }

    /// <summary>R2: an unknown outcome or an infrastructure failure. Everyone reconnects; the next message reloads.</summary>
    private void Poison(string kind, string reason, Exception? exception) {
        ActorLog.Poisoned(_logger, exception, Key, kind, reason);
        foreach (var subscriber in _subs.Values) {
            subscriber.Peer.Restart();
        }
        _subs.Clear();
        _presence.Clear();
        _timers.Clear();
        _runtimeTimers.Clear();
        _cursors.Clear();
        _ring.Reset(_seq);
        _sharedSeq = -1;
        _lastTx = null;
        _state = null;
        if (_status != Status.Stopped) {
            _status = Status.Unloaded;
        }
    }

    private void KickAll(KickCode code) {
        foreach (var subscriber in _subs.Values) {
            subscriber.Peer.Send(Kick(code));
        }
        _subs.Clear();
        _presence.Clear();
    }

    /// <summary>Answers a message that cannot be served: the room could not be loaded, or the actor stopped or retired.</summary>
    private void Abandon(ActorMessage message) {
        switch (message) {
            case SubscribeMessage subscribe:
                subscribe.Peer.Restart();
                break;
            case CommandMessage command:
                command.Peer.Restart();
                break;
            case AskMessage ask:
                ask.Reply.TrySetResult(new AskResult(AskStatus.Unavailable));
                break;
            case QueryMessage query:
                query.Fail(new RoomUnavailableException(Key));
                break;
            case PurgeMessage purge:
                purge.Reply.TrySetResult(_subs.Count == 0);
                break;
            case ShutdownMessage shutdown:
                shutdown.Reply.TrySetResult();
                break;
        }
    }

    /// <summary>
    /// R17: completing both queues is the close, so a racing <c>TryWrite</c> fails instead of being lost; whatever
    /// was buffered before the close goes to the next incarnation. The registry closes the queues under its lock and
    /// installs the next incarnation in the same step, so a sender that saw <see cref="PostResult.Closed"/> finds it.
    /// </summary>
    private void Retire() {
        if (_registry is null) {
            Close().ForEach(Abandon);
        } else {
            _registry.Retire(this, Close);
        }
        _timers.Clear();
        _runtimeTimers.Clear();
        _ring.Release();
    }

    private List<ActorMessage> Close() {
        _closed = true;
        _internal.Writer.TryComplete();
        _external.Writer.TryComplete();
        var leftovers = new List<ActorMessage>();
        while (_internal.Reader.TryRead(out var message) || _external.Reader.TryRead(out message)) {
            leftovers.Add(message);
        }
        return leftovers;
    }

    /// <summary>Step 6 and eviction: rearm the domain's timers and start or stop the idle clock.</summary>
    private void AfterMessage() {
        if (_status == Status.Loaded && !_binding.IsDeleted(_state!)) {
            var now = Now;
            _timers.Reconcile(_binding.Domain.Timers(_state!, _presence.View, now).Select(x => (x.Key, x.DueAt, x.Command)));
        } else {
            _timers.Clear();
        }
        if (_status == Status.Stopped) {
            return;
        }
        if (IsIdle()) {
            if (!_runtimeTimers.IsArmed(RuntimeTimer.Idle)) {
                _runtimeTimers.Arm(RuntimeTimer.Idle, Now + _runtime.Options.IdleEviction, 0);
            }
        } else {
            _runtimeTimers.Cancel(RuntimeTimer.Idle);
        }
    }

    private bool IsIdle() {
        return _subs.Count == 0
            && _timers.Count == 0
            && _runtimeTimers.Keys.All(x => x is RuntimeTimer.IdleTimer);
    }

    private async ValueTask<CachedCursor> CursorAsync(Viewer viewer) {
        if (_cursors.TryGet(viewer.Client, out var cached) && cached.LoadedFor == viewer.Who.Id) {
            return cached;
        }
        var loaded = await _store.LoadCursorAsync(Key, viewer.Client, viewer.Who.Id, CancellationToken.None);
        var cursor = new CachedCursor(viewer.Who.Id, loaded.Owner, loaded.LastQ);
        _cursors.Set(viewer.Client, cursor);
        return cursor;
    }

    private CommandContext Context(Viewer? viewer) => new(viewer, Now, _presence.View, _access);

    /// <summary>
    /// The delegates capture the state they were built for, never the field: a missing or deleted room gets a view
    /// that answers not-found, and a view kept past a poison cannot reach a state that is gone.
    /// </summary>
    private RoomView<TState> View() {
        if (_status != Status.Loaded || _state is not { } state || _binding.IsDeleted(state)) {
            return RoomView<TState>.NotFound(_epoch, _seq, _presence.View);
        }
        return new RoomView<TState>(
            state,
            _epoch,
            _seq,
            _presence.View,
            viewer => _binding.CanSubscribe(state, viewer, _access),
            viewer => SnapFrameAsync(state, viewer, _binding.IsMember(state, viewer, _access), 0));
    }

    private byte[] Ack(long q, bool dup = false) => FrameBytes.Frame(new AckFrame(Key, q, _seq, dup));

    private byte[] Nack(long q, NackCode code) => FrameBytes.Frame(new NackFrame(Key, q, code));

    private byte[] Kick(KickCode code) => FrameBytes.Frame(new KickFrame(Key, code));

    private enum Outcome : byte {
        Applied,
        Rejected,
        Failed,
    }

    private readonly record struct CachedCursor(PrincipalId LoadedFor, CursorOwner Owner, long LastQ);

    private sealed record Subscriber(IRoomPeer Peer, Viewer Viewer, bool Member) {
        public Audience Audience => new(Viewer.Who.Id, Member);
    }

    private abstract record RuntimeTimer {
        public static readonly RuntimeTimer Warm = new WarmTimer();
        public static readonly RuntimeTimer Idle = new IdleTimer();

        public sealed record WarmTimer : RuntimeTimer;

        public sealed record IdleTimer : RuntimeTimer;

        public sealed record Leave(PrincipalId Principal) : RuntimeTimer;

        public sealed record ClientGrace(PrincipalId Principal, ClientId Client) : RuntimeTimer;
    }

    private sealed record SubscribeMessage(IRoomPeer Peer, Viewer Viewer, Epoch? Epoch, long? Seq) : ActorMessage;

    private sealed record CommandMessage(IRoomPeer Peer, Viewer Viewer, CmdFrame Frame) : ActorMessage;

    private sealed record LeaveMessage(ConnectionId Connection) : ActorMessage;

    private sealed record AccessChangedMessage : ActorMessage;

    private sealed record PrincipalUpdatedMessage(PrincipalInfo Principal) : ActorMessage;

    private sealed record PostMessage(TInput Input) : ActorMessage;

    private sealed record AskMessage(TInput Input, Viewer? Viewer, TaskCompletionSource<AskResult> Reply) : ActorMessage;

    private abstract record QueryMessage : ActorMessage {
        /// <summary>Runs the query and returns the action that completes its caller.</summary>
        public abstract Task<Action> RunAsync(RoomView<TState> view);

        public abstract void Fail(Exception exception);
    }

    private sealed record QueryMessage<T>(Func<RoomView<TState>, ValueTask<T>> Query) : QueryMessage {
        public TaskCompletionSource<T> Reply { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override async Task<Action> RunAsync(RoomView<TState> view) {
            try {
                var result = await Query(view);
                return () => Reply.TrySetResult(result);
            } catch (Exception e) {
                return () => Reply.TrySetException(e);
            }
        }

        public override void Fail(Exception exception) => Reply.TrySetException(exception);
    }

    private sealed record DomainTimerFired(string Key, long Generation) : ActorMessage;

    private sealed record RuntimeTimerFired(RuntimeTimer Key, long Generation) : ActorMessage;

    private sealed record PurgeMessage(TaskCompletionSource<bool> Reply) : ActorMessage;

    private sealed record ShutdownMessage(TaskCompletionSource Reply) : ActorMessage;
}

/// <summary>The actor retired between lookup and post; <see cref="ActorRegistry"/> retries on the next incarnation.</summary>
public sealed class ActorClosedException() : Exception("The actor retired; post to its next incarnation.");

internal sealed class ByteContentComparer : IEqualityComparer<byte[]> {
    public static readonly ByteContentComparer Instance = new();

    public bool Equals(byte[]? x, byte[]? y) => x.AsSpan().SequenceEqual(y);

    public int GetHashCode(byte[] obj) {
        var hash = new HashCode();
        hash.AddBytes(obj);
        return hash.ToHashCode();
    }
}
