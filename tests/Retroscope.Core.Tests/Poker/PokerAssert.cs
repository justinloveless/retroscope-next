using System.Text.Json;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Core.Tests.Poker;

internal static class PokerAssert {
    public static Decision<PokerEvent>.Reject Rejected(Decision<PokerEvent> decision, NackCode code) {
        var reject = Assert.IsType<Decision<PokerEvent>.Reject>(decision);
        Assert.Equal(code, reject.Code);
        return reject;
    }

    public static void NoOp(Decision<PokerEvent> decision) => Assert.IsType<Decision<PokerEvent>.NoOp>(decision);

    public static IReadOnlyList<PokerEvent> Accepted(Decision<PokerEvent> decision) {
        return Assert.IsType<Decision<PokerEvent>.Accept>(decision).Events;
    }

    /// <summary>The event kinds of a decision, ignoring memory-only bookkeeping.</summary>
    public static string[] Kinds(IReadOnlyList<PokerEvent> events) {
        return events.Where(x => x.Durability != EventDurability.MemoryOnly).Select(x => x.GetType().Name).ToArray();
    }

    public static JsonElement Current(Decision<PokerEvent>.Reject reject) {
        Assert.NotNull(reject.Data);
        return reject.Data.Value.GetProperty("current");
    }
}
