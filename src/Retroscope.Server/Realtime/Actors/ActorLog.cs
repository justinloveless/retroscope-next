using Retroscope.Core.Ids;

namespace Retroscope.Server.Realtime.Actors;

// Command payloads are never logged (S33): kinds and SQL states only.
internal static partial class ActorLog {
    [LoggerMessage(1, LogLevel.Error, "Room {Room} could not be loaded")]
    public static partial void LoadFailed(ILogger logger, Exception exception, RoomKey room);

    [LoggerMessage(2, LogLevel.Error, "Room {Room} rejected the transaction for {Kind} (SQLSTATE {SqlState})")]
    public static partial void CommitRejected(ILogger logger, Exception exception, RoomKey room, string kind, string? sqlState);

    [LoggerMessage(3, LogLevel.Warning, "Room {Room} poisoned after {Kind}: {Reason}; subscribers reconnect and it reloads")]
    public static partial void Poisoned(ILogger logger, Exception? exception, RoomKey room, string kind, string reason);

    [LoggerMessage(4, LogLevel.Error, "Room {Room} failed handling {Message}")]
    public static partial void HandlerFailed(ILogger logger, Exception exception, RoomKey room, string message);

    [LoggerMessage(5, LogLevel.Debug, "Room {Room} rejected internal input {Kind} with {Code}")]
    public static partial void InternalRejected(ILogger logger, RoomKey room, string kind, string code);
}
