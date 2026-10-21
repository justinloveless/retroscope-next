namespace Retroscope.Core.Room;

public interface IRoomDomain<TState, TCommand, TEvent> {
    /// <summary>Pure: reads <paramref name="state"/>, never mutates it. Authorization comes first.</summary>
    Decision<TEvent> Decide(TState state, TCommand command, CommandContext context);

    /// <summary>Mutates <paramref name="state"/>; called only after the events committed.</summary>
    void Apply(TState state, TEvent evt);

    /// <summary>Spotlight unbind, pending-round discard and similar reactions to presence.</summary>
    IReadOnlyList<TEvent> OnPresence(TState state, PresenceDelta delta, PresenceView presence, DateTimeOffset now);

    /// <summary>Every timer that should be armed for this state (auto-reveal, retro timer, spotlight check).</summary>
    IReadOnlyList<TimerRequest<TCommand>> Timers(TState state, PresenceView presence, DateTimeOffset now);

    /// <summary>Runs once at the warm-up deadline after load (R7, R8, R14).</summary>
    IReadOnlyList<TEvent> OnWarm(TState state, PresenceView presence, DateTimeOffset now);
}
