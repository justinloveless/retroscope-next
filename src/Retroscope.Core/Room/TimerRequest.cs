namespace Retroscope.Core.Room;

/// <summary>
/// A timer the domain wants armed. <paramref name="Key"/> names the logical timer (one armed timer per key;
/// a new request with the same key replaces the old one). When it fires the actor decides
/// <paramref name="Command"/> as an internal command.
/// </summary>
public sealed record TimerRequest<TCommand>(string Key, DateTimeOffset DueAt, TCommand Command);
