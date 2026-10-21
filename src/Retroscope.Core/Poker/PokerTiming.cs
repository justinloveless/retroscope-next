namespace Retroscope.Core.Poker;

public static class PokerTiming {
    /// <summary>After load, auto-reveal waits this long so reconnecting clients can re-establish presence.</summary>
    public static readonly TimeSpan WarmUp = TimeSpan.FromSeconds(15);

    /// <summary>Quiet time after the last vote or presence change before an auto-reveal fires.</summary>
    public static readonly TimeSpan AutoRevealDelay = TimeSpan.FromMilliseconds(600);

    /// <summary>How long the spotlight survives its holder's absence (R6).</summary>
    public static readonly TimeSpan SpotlightGrace = TimeSpan.FromMinutes(3);

    /// <summary>A moved spotlight is persisted once it has been still this long (§c.8).</summary>
    public static readonly TimeSpan SpotlightSettle = TimeSpan.FromMilliseconds(500);
}
