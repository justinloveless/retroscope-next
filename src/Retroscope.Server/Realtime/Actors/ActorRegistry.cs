using System.Collections.Concurrent;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Server.Realtime.Actors;

/// <summary>
/// Every live room actor, by room key (R17). <see cref="GetOrStartAsync"/> starts an actor on first use; an actor
/// retires itself after idling, and a post that loses that race is retried on the next incarnation. A key being
/// purged (<see cref="TryPurgeAsync"/>) is closed: nobody can start or load it until the purge finishes (R18).
/// </summary>
public sealed class ActorRegistry {
    private const int MaxAttempts = 100;

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
    public async ValueTask<IRoomActor> GetOrStartAsync(RoomKey key, CancellationToken cancellationToken) {
        while (true) {
            if (_actors.TryGetValue(key, out var live)) {
                return live;
            }
            Task? closing;
            lock (_lock) {
                if (_closing.TryGetValue(key, out var gate)) {
                    closing = gate.Task;
                } else {
                    return _actors.GetOrAdd(key, Start);
                }
            }
            await closing.WaitAsync(cancellationToken);
        }
    }

    public async ValueTask<PostResult> SubscribeAsync(
        RoomKey key,
        IRoomPeer peer,
        Viewer viewer,
        Epoch? epoch,
        long? seq,
        CancellationToken cancellationToken) {
        return await SendAsync(key, actor => actor.Subscribe(peer, viewer, epoch, seq), cancellationToken);
    }

    public async ValueTask<PostResult> CommandAsync(
        RoomKey key,
        IRoomPeer peer,
        Viewer viewer,
        CmdFrame frame,
        CancellationToken cancellationToken) {
        return await SendAsync(key, actor => actor.Command(peer, viewer, frame), cancellationToken);
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
    public async Task<AskResult> AskAsync<TState, TInput>(
        RoomKey key,
        TInput input,
        Viewer? viewer,
        CancellationToken cancellationToken)
        where TState : class
        where TInput : class {
        for (var attempt = 0; ; attempt++) {
            var actor = Typed<TState, TInput>(await GetOrStartAsync(key, cancellationToken));
            try {
                return await actor.AskAsync(input, viewer, cancellationToken);
            } catch (ActorClosedException) when (attempt < MaxAttempts) {
                await Task.Yield();
            }
        }
    }

    public async Task<T> QueryAsync<TState, TInput, T>(
        RoomKey key,
        Func<RoomView<TState>, ValueTask<T>> query,
        CancellationToken cancellationToken)
        where TState : class
        where TInput : class {
        for (var attempt = 0; ; attempt++) {
            var actor = Typed<TState, TInput>(await GetOrStartAsync(key, cancellationToken));
            try {
                return await actor.QueryAsync(query, cancellationToken);
            } catch (ActorClosedException) when (attempt < MaxAttempts) {
                await Task.Yield();
            }
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
    /// Called by a retiring actor: removes exactly that incarnation and hands what it still had queued to the next
    /// one, which takes those messages before anything posted after the close.
    /// </summary>
    internal void Retired(IRoomActor actor, IReadOnlyList<ActorMessage> leftovers) {
        lock (_lock) {
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

    private IRoomActor Start(RoomKey key) => Start(key, []);

    private IRoomActor Start(RoomKey key, IReadOnlyList<ActorMessage> backlog) {
        if (!_types.TryGetValue(key.Kind, out var type)) {
            throw new InvalidOperationException($"No room type is registered for {key.Kind} rooms.");
        }
        return type.Start(key, _runtime, this, backlog);
    }

    private async ValueTask<PostResult> SendAsync(RoomKey key, Func<IRoomActor, PostResult> send, CancellationToken cancellationToken) {
        for (var attempt = 0; attempt < MaxAttempts; attempt++) {
            var result = send(await GetOrStartAsync(key, cancellationToken));
            if (result != PostResult.Closed) {
                return result;
            }
            await Task.Yield();
        }
        return PostResult.Closed;
    }

    private void SendIfLive(RoomKey key, Func<IRoomActor, PostResult> send) {
        for (var attempt = 0; attempt < MaxAttempts && _actors.TryGetValue(key, out var actor); attempt++) {
            if (send(actor) != PostResult.Closed) {
                return;
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
