using Retroscope.Core.Authz;
using Retroscope.Core.Room;

namespace Retroscope.Core.Poker;

/// <summary>The poker room's domain for the actor runtime: decide, apply, react to presence, timers and warm-up.</summary>
public sealed class PokerDomain(PolicyCheck? can = null) : IRoomDomain<PokerState, PokerInput, PokerEvent> {
    private readonly PolicyCheck _can = can ?? Policy.Can;

    public Decision<PokerEvent> Decide(PokerState state, PokerInput command, CommandContext context) {
        return PokerDecider.Decide(state, command, context, _can);
    }

    public void Apply(PokerState state, PokerEvent evt) => PokerApply.Apply(state, evt);

    public IReadOnlyList<PokerEvent> OnPresence(
        PokerState state,
        PresenceDelta delta,
        PresenceView presence,
        DateTimeOffset now) {
        return PokerReactions.OnPresence(state, delta, presence, now);
    }

    public IReadOnlyList<TimerRequest<PokerInput>> Timers(PokerState state, PresenceView presence, DateTimeOffset now) {
        return PokerReactions.Timers(state, presence, now);
    }

    public IReadOnlyList<PokerEvent> OnWarm(PokerState state, PresenceView presence, DateTimeOffset now) {
        return PokerReactions.OnWarm(state, presence, now);
    }
}
