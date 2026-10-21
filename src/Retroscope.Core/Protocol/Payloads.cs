namespace Retroscope.Core.Protocol;

/// <summary>The <c>d</c> of a <c>cmd</c> frame. Each concrete type has exactly one kind in <see cref="Catalog"/>.</summary>
public abstract record Command;

/// <summary>Commands addressed to a <c>poker:</c> room. Domain-internal commands may derive from it too.</summary>
public abstract record PokerCommand : Command;

/// <summary>Commands addressed to a <c>board:</c> room.</summary>
public abstract record RetroCommand : Command;

/// <summary>Commands addressed to a <c>team:</c> room.</summary>
public abstract record TeamCommand : Command;

/// <summary>One <c>{k,d}</c> entry of a tx frame, already projected for its viewer.</summary>
public abstract record WireEvent;

/// <summary>The <c>d</c> of an unsequenced <c>x</c> frame: idempotent, overwrite-only cache fill.</summary>
public abstract record Unsequenced;

/// <summary>The <c>d</c> of a <c>snap</c> frame: the public projection, shared by every viewer.</summary>
public abstract record RoomSnapshot;

/// <summary>The <c>o</c> of a <c>snap</c> frame: the few hundred bytes that differ per viewer (R11).</summary>
public abstract record RoomOverlay;
