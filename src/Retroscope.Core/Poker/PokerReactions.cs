using Retroscope.Core.Room;

namespace Retroscope.Core.Poker;

/// <summary>What the room does on its own: presence reactions, warm-up and the timers it wants armed.</summary>
public static class PokerReactions {
    public const string SpotlightGraceTimer = "spotlight-grace";
    public const string SpotlightSettleTimer = "spotlight-settle";

    public static string AutoRevealTimer(Ids.RoundId round) => $"auto-reveal:{round}";

    /// <summary>
    /// A join or leave re-arms auto-reveal (R14). The holder leaving starts the 3-minute spotlight grace; the holder's
    /// bound tab gone unbinds it; a pending round's owner leaving discards their previews (R6, R7, R8).
    /// </summary>
    public static IReadOnlyList<PokerEvent> OnPresence(
        PokerState state,
        PresenceDelta delta,
        PresenceView presence,
        DateTimeOffset now) {
        var changes = new PokerChanges(state);
        switch (delta.Change) {
            case PresenceChange.Joined:
                changes.ArmWatchedRounds(now);
                if (state.Spotlight is { HolderAbsentSince: not null } joined && joined.Holder == delta.Principal) {
                    changes.Add(new HolderAbsence(null));
                }
                break;
            case PresenceChange.Left:
                changes.ArmWatchedRounds(now);
                if (state.Spotlight is { } left && left.Holder == delta.Principal) {
                    changes.Add(new HolderAbsence(now));
                }
                changes.DiscardPending(delta.Principal);
                break;
            case PresenceChange.ClientGone:
                if (state.Spotlight is { } bound
                    && bound.Holder == delta.Principal
                    && bound.HolderClient is { } client
                    && client == delta.Client) {
                    changes.Add(new SpotlightChanged(bound.Holder, null, bound.RoundId));
                }
                break;
        }
        return changes.Events;
    }

    /// <summary>
    /// At the warm-up deadline: arm auto-reveal for every watched round, release a spotlight whose holder is not
    /// present, and discard pending rounds whose owner is absent (R7, R8, R14).
    /// </summary>
    public static IReadOnlyList<PokerEvent> OnWarm(PokerState state, PresenceView presence, DateTimeOffset now) {
        var changes = new PokerChanges(state);
        changes.ArmWatchedRounds(now);
        if (state.Spotlight is { } spotlight && !presence.IsPresent(spotlight.Holder)) {
            changes.SetSpotlight(null, null, null);
        }
        var owners = state.Rounds.Values
            .Where(x => x.Pending && x.PendingOwner is { } owner && !presence.IsPresent(owner))
            .Select(x => x.PendingOwner!.Value)
            .Distinct()
            .ToArray();
        foreach (var owner in owners) {
            changes.DiscardPending(owner);
        }
        return changes.Events;
    }

    /// <summary>
    /// Every timer that should be armed now. A key that is not returned should be cancelled; a returned key replaces
    /// the armed one.
    /// </summary>
    public static IReadOnlyList<TimerRequest<PokerInput>> Timers(PokerState state, PresenceView presence, DateTimeOffset now) {
        var timers = new List<TimerRequest<PokerInput>>();
        if (now >= state.WarmUntil) {
            foreach (var id in state.Order) {
                var round = state.Rounds[id];
                if (AutoReveal.IsReady(state, round, presence)) {
                    timers.Add(new TimerRequest<PokerInput>(
                        AutoRevealTimer(id),
                        round.ArmedAt + PokerTiming.AutoRevealDelay,
                        new AutoRevealDue(id, round.AutoRevealGen)));
                }
            }
        }
        if (state.Spotlight is { } spotlight) {
            if (spotlight.HolderAbsentSince is { } since && !presence.IsPresent(spotlight.Holder)) {
                timers.Add(new TimerRequest<PokerInput>(
                    SpotlightGraceTimer, since + PokerTiming.SpotlightGrace, new SpotlightGraceDue(spotlight.Holder)));
            }
            if (spotlight.Dirty) {
                timers.Add(new TimerRequest<PokerInput>(
                    SpotlightSettleTimer, spotlight.MovedAt + PokerTiming.SpotlightSettle, new SpotlightSettleDue()));
            }
        }
        return timers;
    }
}
