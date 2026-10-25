using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Server.Realtime.Actors;

/// <summary>
/// What a room type plugs into the generic runtime: its domain, how client commands and events map, who may
/// subscribe, and the projections (U21–U23 implement one per room kind).
/// </summary>
public interface IRoomBinding<TState, TInput, TEvent> where TState : class where TInput : class {
    RoomKind Kind { get; }

    IRoomDomain<TState, TInput, TEvent> Domain { get; }

    /// <summary>Whether the room sends <c>presence.*</c> events and lists presence in its snapshot.</summary>
    bool TracksPresence => true;

    /// <summary>Wraps a parsed client command; null when the command does not belong to this room kind.</summary>
    TInput? FromClient(Command command);

    /// <summary>
    /// Whether <paramref name="command"/> creates something under a client-minted id. Deciders answer a create whose id
    /// already exists with the same payload with a no-op (UF6): it committed under an earlier <c>(cid, q)</c>, so the
    /// ack says <c>dup</c>.
    /// </summary>
    bool IsCreate(Command command) => false;

    /// <summary>
    /// <see cref="EventDurability.Committed"/> events are written in the command's transaction,
    /// <see cref="EventDurability.Ephemeral"/> ones are sequenced without one and
    /// <see cref="EventDurability.MemoryOnly"/> ones never reach the wire.
    /// </summary>
    EventDurability DurabilityOf(TEvent evt) => EventDurability.Committed;

    /// <summary><c>Policy.Can(viewer, Subscribe, room)</c> (§c.11), re-run on <c>AccessChanged</c>.</summary>
    bool CanSubscribe(TState state, Viewer viewer, IAccessView access);

    /// <summary>Selects the shared snapshot variant (S12: the roster goes to team members only).</summary>
    bool IsMember(TState state, Viewer viewer, IAccessView access) => false;

    /// <param name="presence">Present principals in canonical order (pid as lower-case string, ordinal).</param>
    RoomSnapshot Snapshot(TState state, IReadOnlyList<PrincipalInfo> presence, bool forMembers);

    RoomOverlay? Overlay(TState state, Viewer viewer);

    /// <summary>The tx events one principal sees, projected from the state after <paramref name="events"/>.</summary>
    IReadOnlyList<WireEvent> Project(TState state, IReadOnlyList<TEvent> events, PrincipalId viewer, bool viewerIsMember);

    /// <summary>
    /// True when <paramref name="latest"/> makes the previous transition redundant (consecutive spotlight moves), so
    /// the ring keeps only its seq with no events (§c.8).
    /// </summary>
    bool Supersedes(IReadOnlyList<TEvent> latest, IReadOnlyList<TEvent> previous) => false;

    /// <summary>When <see cref="IRoomDomain{TState,TCommand,TEvent}.OnWarm"/> runs; null runs it right after load.</summary>
    DateTimeOffset? WarmUntil(TState state) => null;

    /// <summary>Once true every subscriber is kicked with <c>deleted</c> and new subscriptions get <c>not_found</c>.</summary>
    bool IsDeleted(TState state) => false;

    /// <summary>The team the room belongs to; an admitted subscription counts as a visit to it (UX18).</summary>
    TeamId? TeamOf(TState state) => null;

    /// <summary>The last input decided on a graceful shutdown (persist a moving spotlight), or null.</summary>
    TInput? OnShutdown(TState state) => null;
}
