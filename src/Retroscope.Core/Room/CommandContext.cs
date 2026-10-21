namespace Retroscope.Core.Room;

/// <param name="Viewer">The caller; null for internal transitions (timers, presence, actor-to-actor posts).</param>
public sealed record CommandContext(Viewer? Viewer, DateTimeOffset Now, PresenceView Presence, IAccessView Access);
