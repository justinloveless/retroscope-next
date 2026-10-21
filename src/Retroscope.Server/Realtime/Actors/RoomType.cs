using Retroscope.Core.Ids;
using Retroscope.Core.Room;

namespace Retroscope.Server.Realtime.Actors;

/// <summary>One room kind the registry can start actors for (rooms register one through <see cref="ActorsModule.AddRoom"/>).</summary>
public abstract class RoomType {
    private protected RoomType(RoomKind kind) {
        Kind = kind;
    }

    public RoomKind Kind { get; }

    public static RoomType Of<TState, TInput, TEvent>(
        IRoomBinding<TState, TInput, TEvent> binding,
        IRoomStore<TState, TEvent> store,
        IAccessView access)
        where TState : class
        where TInput : class {
        return new Typed<TState, TInput, TEvent>(binding, store, access);
    }

    internal abstract IRoomActor Start(RoomKey key, ActorRuntime runtime, ActorRegistry registry, IReadOnlyList<ActorMessage> backlog);

    private sealed class Typed<TState, TInput, TEvent>(
        IRoomBinding<TState, TInput, TEvent> binding,
        IRoomStore<TState, TEvent> store,
        IAccessView access) : RoomType(binding.Kind)
        where TState : class
        where TInput : class {
        internal override IRoomActor Start(
            RoomKey key,
            ActorRuntime runtime,
            ActorRegistry registry,
            IReadOnlyList<ActorMessage> backlog) {
            return new RoomActor<TState, TInput, TEvent>(key, binding, store, access, runtime, registry, backlog);
        }
    }
}
