using System.Collections.Concurrent;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Server.Realtime.Actors;

/// <summary>
/// Every live room actor, by room key (R17). <see cref="GetOrStartAsync"/> starts an actor on first use; an actor
/// retires itself after idling, and a post that loses that race goes to the next incarnation, which the retiring
/// actor installs in the same locked step that closes its queues. A key being purged (<see cref="TryPurgeAsync"/>)
/// is closed: nobody can start or load it until the purge finishes (R18), and a caller waits for that at most
/// <see cref="ActorOptions.IncarnationWait"/> before it is answered <c>server_busy</c>.
/// </summary>
public sealed class ActorRegistry {
    private readonly ConcurrentDictionary<RoomKey, IRoomActor> _actors = new();
    private readonly Dictionary<RoomKey, TaskCompletionSource> _closing = [];
    private readonly Dictionary<RoomKey, List<ActorMessage>> _deferred = [];
    private readonly Lock _lock = new();
    private readonly ActorRuntime _runtime;
    private readonly Dictionary<RoomKind, RoomType> _types;

    public ActorRegistry(ActorRuntime runtime, IEnumerable<RoomType> types) {
        _runtime = runtime;
        _types = types.ToDictionary(x => x.Kind);
    }

    public int Count => _actors.Count;

    public bool TryGet(RoomKey key, out IRoomActor actor) => _actors.TryGetValue(key, out actor!);

    /// <summary>The live actor for <paramref name="key"/>, started if needed; waits while the key is being purged.</summary>
    /// <exception cref="TimeoutException">The purge of the key outlasted <see cref="ActorOptions.IncarnationWait"/>.</exception>
    public ValueTask<IRoomActor> GetOrStartAsync(RoomKey key, CancellationToken cancellationToken) {
        return _actors.TryGetValue(key, out var live) ? ValueTask.FromResult(live) : CurrentAsync(key, cancellationToken);
    }

    /// <returns><see cref="PostResult.Busy"/> when the room could not take it now; the transport decides.</returns>
    public async ValueTask<PostResult> SubscribeAsync(
        RoomKey key,
        IRoomPeer peer,
        Viewer viewer,
        Epoch? epoch,
        long? seq,
        CancellationToken cancellationToken) {
        try {
            return await SendAsync(key, actor => actor.Subscribe(peer, viewer, epoch, seq), cancellationToken);
        } catch (TimeoutException) {
            return PostResult.Busy;
        }
    }

    /// <returns><see cref="PostResult.Busy"/> after the peer was nacked <c>server_busy</c>.</returns>
    public async ValueTask<PostResult> CommandAsync(
        RoomKey key,
        IRoomPeer peer,
        Viewer viewer,
        CmdFrame frame,
        CancellationToken cancellationToken) {
        try {
            return await SendAsync(key, actor => actor.Command(peer, viewer, frame), cancellationToken);
        } catch (TimeoutException) {
            peer.Send(FrameBytes.Frame(new NackFrame(key, frame.Q, NackCode.ServerBusy)));
            return PostResult.Busy;
        }
    }

    /// <summary>Only a live actor can hold the subscription, so nothing is started.</summary>
    public void Unsubscribe(RoomKey key, ConnectionId connection) => SendIfLive(key, x => x.Unsubscribe(connection));

    public void ConnectionClosed(RoomKey key, ConnectionId connection) => SendIfLive(key, x => x.ConnectionClosed(connection));

    /// <summary>A room that is not live loads fresh access on its next start.</summary>
    public void AccessChanged(RoomKey key) => SendIfLive(key, x => x.AccessChanged());

    public void PrincipalUpdated(RoomKey key, PrincipalInfo principal) => SendIfLive(key, x => x.PrincipalUpdated(principal));

    /// <summary>
    /// An actor-to-actor post to a live room: a synchronous enqueue, never awaited (§c.2). A room that is not live
    /// loads the posted change from the database when it next starts, so nothing is started for it.
    /// </summary>
    public void Post<TState, TInput>(RoomKey key, TInput input) where TState : class where TInput : class {
        SendIfLive(key, actor => Typed<TState, TInput>(actor).Post(input));
    }

    /// <summary>An HTTP-originated change (§c.2: an HTTP handler may ask, an actor never does).</summary>
    /// <returns><see cref="AskStatus.Unavailable"/> with <c>server_busy</c> when the room did not start in time.</returns>
    public async Task<AskResult> AskAsync<TState, TInput>(
        RoomKey key,
        TInput input,
        Viewer? viewer,
        CancellationToken cancellationToken)
        where TState : class
        where TInput : class {
        try {
            var actor = await GetOrStartAsync(key, cancellationToken);
            while (true) {
                try {
                    return await Typed<TState, TInput>(actor).AskAsync(input, viewer, cancellationToken);
                } catch (ActorClosedException) {
                    actor = await CurrentAsync(key, cancellationToken);
                }
            }
        } catch (TimeoutException) {
            return new AskResult(AskStatus.Unavailable, Code: NackCode.ServerBusy);
        }
    }

    /// <exception cref="RoomUnavailableException">The room did not start in time, could not be loaded or stopped.</exception>
    public async Task<T> QueryAsync<TState, TInput, T>(
        RoomKey key,
        Func<RoomView<TState>, ValueTask<T>> query,
        CancellationToken cancellationToken)
        where TState : class
        where TInput : class {
        try {
            var actor = await GetOrStartAsync(key, cancellationToken);
            while (true) {
                try {
                    return await Typed<TState, TInput>(actor).QueryAsync(query, cancellationToken);
                } catch (ActorClosedException) {
                    actor = await CurrentAsync(key, cancellationToken);
                }
            }
        } catch (TimeoutException e) {
            throw new RoomUnavailableException(key, e);
        }
    }

    /// <summary>
    /// Runs <paramref name="purge"/> (retention deletes) while the key is closed: the live actor retires first, and
    /// nothing starts or loads the room until the purge finished. False, without purging, when someone is
    /// subscribed or waiting, or when the key is already being purged.
    /// </summary>
    public async Task<bool> TryPurgeAsync(RoomKey key, Func<CancellationToken, Task> purge, CancellationToken cancellationToken) {
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IRoomActor? live;
        lock (_lock) {
            if (!_closing.TryAdd(key, gate)) {
                return false;
            }
            _actors.TryGetValue(key, out live);
        }
        try {
            if (live is IClosable closable && !await closable.TryCloseForPurgeAsync()) {
                return false;
            }
            await purge(cancellationToken);
            return true;
        } finally {
            lock (_lock) {
                _closing.Remove(key);
                if (_deferred.Remove(key, out var leftovers)) {
                    _actors[key] = Start(key, leftovers);
                }
            }
            gate.SetResult();
        }
    }

    /// <summary>The <c>DrainActors</c> shutdown step: every actor finishes its current message and sends <c>bye</c>.</summary>
    public async Task ShutdownAsync(CancellationToken cancellationToken) {
        var actors = _actors.Values.OfType<IClosable>().Select(x => x.ShutdownAsync());
        await Task.WhenAll(actors).WaitAsync(cancellationToken);
    }

    /// <summary>
    /// Called by a retiring actor: <paramref name="close"/> completes its queues and returns what they still held,
    /// and in the same locked step exactly that incarnation is removed and the leftovers go to the next one, which
    /// takes them before anything posted after the close.
    /// </summary>
    internal void Retire(IRoomActor actor, Func<IReadOnlyList<ActorMessage>> close) {
        lock (_lock) {
            var leftovers = close();
            if (leftovers.Count == 0) {
                _actors.TryRemove(new KeyValuePair<RoomKey, IRoomActor>(actor.Key, actor));
                return;
            }
            if (_closing.ContainsKey(actor.Key)) {
                _actors.TryRemove(new KeyValuePair<RoomKey, IRoomActor>(actor.Key, actor));
                if (!_deferred.TryGetValue(actor.Key, out var deferred)) {
                    _deferred[actor.Key] = deferred = [];
                }
                deferred.AddRange(leftovers);
                return;
            }
            _actors.TryUpdate(actor.Key, Start(actor.Key, leftovers), actor);
        }
    }

    private IRoomActor Start(RoomKey key, IReadOnlyList<ActorMessage> backlog) {
        if (!_types.TryGetValue(key.Kind, out var type)) {
            throw new InvalidOperationException($"No room type is registered for {key.Kind} rooms.");
        }
        return type.Start(key, _runtime, this, backlog);
    }

    /// <summary>
    /// The incarnation to post to, read under the lock: an actor closes its queues inside <see cref="Retire"/>, so
    /// after a <see cref="PostResult.Closed"/> this never returns the closed one again.
    /// </summary>
    private async ValueTask<IRoomActor> CurrentAsync(RoomKey key, CancellationToken cancellationToken) {
        while (true) {
            Task closing;
            lock (_lock) {
                if (_actors.TryGetValue(key, out var live)) {
                    return live;
                }
                if (!_closing.TryGetValue(key, out var gate)) {
                    return _actors[key] = Start(key, []);
                }
                closing = gate.Task;
            }
            await closing.WaitAsync(_runtime.Options.IncarnationWait, _runtime.Time, cancellationToken);
        }
    }

    private async ValueTask<PostResult> SendAsync(RoomKey key, Func<IRoomActor, PostResult> send, CancellationToken cancellationToken) {
        var actor = await GetOrStartAsync(key, cancellationToken);
        var result = send(actor);
        while (result == PostResult.Closed) {
            actor = await CurrentAsync(key, cancellationToken);
            result = send(actor);
        }
        return result;
    }

    private void SendIfLive(RoomKey key, Func<IRoomActor, PostResult> send) {
        if (!_actors.TryGetValue(key, out var actor)) {
            return;
        }
        while (send(actor) == PostResult.Closed) {
            lock (_lock) {
                if (!_actors.TryGetValue(key, out actor)) {
                    return;
                }
            }
        }
    }

    private static IRoomActor<TState, TInput> Typed<TState, TInput>(IRoomActor actor) where TState : class where TInput : class {
        return actor as IRoomActor<TState, TInput>
            ?? throw new InvalidOperationException($"Room {actor.Key} does not take {typeof(TInput).Name}.");
    }
}

/// <summary>The registry's side of an actor: purge and shutdown.</summary>
internal interface IClosable {
    Task<bool> TryCloseForPurgeAsync();

    Task ShutdownAsync();
}
