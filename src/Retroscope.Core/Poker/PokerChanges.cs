using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Core.Poker;

/// <summary>
/// Collects the events of one decision and applies each to a private copy of the state as it is added, so the
/// later steps of a composite transition (next round, delete, spotlight hand-over) read the state the earlier steps
/// produced. The actor's state is never touched.
/// </summary>
internal sealed class PokerChanges(PokerState original) {
    private PokerState? _scratch;

    public List<PokerEvent> Events { get; } = [];

    public PokerState State => _scratch ?? original;

    public void Add(PokerEvent evt) {
        _scratch ??= original.Clone();
        PokerApply.Apply(_scratch, evt);
        Events.Add(evt);
    }

    public Decision<PokerEvent> ToDecision() {
        return Events.Count == 0 ? new Decision<PokerEvent>.NoOp() : new Decision<PokerEvent>.Accept(Events);
    }

    /// <summary>
    /// Removes a round with the spotlight repaired first (moved to the pointer round, or released) and the pointer
    /// repaired after (R9). The spotlight FKs are not cascading, so a missed repair would fail at commit.
    /// </summary>
    public void RemoveRound(RoundId id) {
        var pointerRemoved = State.CurrentRoundId == id;
        var pointer = pointerRemoved ? Fallback(State, id) : State.CurrentRoundId;
        if (State.Spotlight is { } spotlight && spotlight.RoundId == id) {
            Add(pointer is { } target
                ? new SpotlightChanged(spotlight.Holder, spotlight.HolderClient, target)
                : new SpotlightChanged(null, null, null));
        }
        Add(new RoundDeleted(id));
        if (pointerRemoved && pointer is { } repaired) {
            Add(new PointerMoved(repaired, PointerCause.Repair, false));
        }
    }

    /// <summary>Discards every pending round <paramref name="owner"/> previews (R8).</summary>
    public void DiscardPending(PrincipalId owner) {
        foreach (var id in State.Order.Where(x => State.Rounds[x].Pending && State.Rounds[x].PendingOwner == owner)
            .ToArray()) {
            RemoveRound(id);
        }
    }

    /// <summary>Hands the spotlight to a new holder (or releases it), discarding the previous holder's pending rounds.</summary>
    public void SetSpotlight(PrincipalId? holder, ClientId? client, RoundId? round) {
        var previous = State.Spotlight?.Holder;
        Add(new SpotlightChanged(holder, client, round));
        if (previous is { } old && old != holder) {
            DiscardPending(old);
        }
    }

    public void ArmWatchedRounds(DateTimeOffset now) {
        foreach (var id in State.Order) {
            if (AutoReveal.IsWatched(State.Rounds[id])) {
                Add(new AutoRevealArmed(id, now));
            }
        }
    }

    /// <summary>Where the pointer goes when its round disappears: the highest-numbered remaining round, preferring committed ones.</summary>
    private static RoundId? Fallback(PokerState state, RoundId removed) {
        var remaining = state.Order.Where(x => x != removed).Select(x => state.Rounds[x]).ToArray();
        var pick = remaining.Where(x => !x.Pending).MaxBy(x => x.Number) ?? remaining.MaxBy(x => x.Number);
        return pick?.Id;
    }
}
