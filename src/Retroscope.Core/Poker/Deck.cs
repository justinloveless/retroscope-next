namespace Retroscope.Core.Poker;

public static class Deck {
    public static readonly short[] Values = [1, 2, 3, 5, 8, 13, 21];

    public static bool Contains(short points) => Array.IndexOf(Values, points) >= 0;

    /// <summary>The upper end of a "between" vote starting at <paramref name="points"/>; null at the top of the deck.</summary>
    public static short? Next(short points) {
        var index = Array.IndexOf(Values, points);
        return index >= 0 && index + 1 < Values.Length ? Values[index + 1] : null;
    }
}
