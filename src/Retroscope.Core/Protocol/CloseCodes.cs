namespace Retroscope.Core.Protocol;

/// <summary>WebSocket close codes (DESIGN §c.3).</summary>
public static class CloseCodes {
    /// <summary>Restart, fencing loss, DB outage or an outcome-unknown commit; the client reconnects.</summary>
    public const int Restart = 1012;
    public const int SlowConsumer = 4008;
    public const int ProtocolViolation = 4400;
    public const int Unauthenticated = 4401;
    public const int BadOrigin = 4403;
    public const int CidInUse = 4409;
    public const int FrameTooLarge = 4413;
    public const int RateLimited = 4429;

    public static IReadOnlyList<(string Name, int Code)> All { get; } = [
        (nameof(Restart), Restart),
        (nameof(SlowConsumer), SlowConsumer),
        (nameof(ProtocolViolation), ProtocolViolation),
        (nameof(Unauthenticated), Unauthenticated),
        (nameof(BadOrigin), BadOrigin),
        (nameof(CidInUse), CidInUse),
        (nameof(FrameTooLarge), FrameTooLarge),
        (nameof(RateLimited), RateLimited),
    ];
}
