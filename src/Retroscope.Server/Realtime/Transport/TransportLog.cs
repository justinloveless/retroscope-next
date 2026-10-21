using Retroscope.Core.Ids;

namespace Retroscope.Server.Realtime.Transport;

// Never logs frame contents (S33): parser messages name members and kinds only.
internal static partial class TransportLog {
    [LoggerMessage(Level = LogLevel.Information, Message = "WebSocket refused with {Code}: {Reason}")]
    public static partial void Refused(ILogger logger, int code, string reason);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connection {Connection} closed for a protocol violation: {Problem}")]
    public static partial void ProtocolViolation(ILogger logger, ConnectionId connection, string problem);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connection {Connection} closed as a slow consumer with {PendingBytes} bytes pending")]
    public static partial void SlowConsumer(ILogger logger, ConnectionId connection, long pendingBytes);

    [LoggerMessage(Level = LogLevel.Information, Message = "Connection {Connection} closed for sustained rate abuse")]
    public static partial void RateAbuse(ILogger logger, ConnectionId connection);

    [LoggerMessage(Level = LogLevel.Error, Message = "Connection {Connection} failed")]
    public static partial void ConnectionFailed(ILogger logger, Exception exception, ConnectionId connection);
}
