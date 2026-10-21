namespace Retroscope.Core.Poker;

/// <summary>
/// A line-for-line port of <c>getWinningVoteFromSelections</c> (<c>orig/src/hooks/usePokerSession.ts:52-82</c> at
/// 7f48a79), checked against <c>tests/golden/poker-scoring.json</c>.
/// </summary>
public static class Scoring {
    /// <summary>
    /// Abstains are excluded, a "between" vote adds weight 1 to each end, the plurality wins, ties go to the
    /// higher value, and no participants gives 0.
    /// </summary>
    public static short Winner(IEnumerable<Vote> votes) {
        return (short)Winner(votes.Where(x => !x.Abstained && x.Points is not null)
            .Select(x => ((int)x.Points!.Value, (int?)x.High)));
    }

    /// <param name="participating">Non-abstaining selections: points and the optional "between" upper end.</param>
    public static int Winner(IEnumerable<(int Points, int? High)> participating) {
        var weights = new List<KeyValuePair<int, int>>();
        foreach (var (points, high) in participating) {
            if (high is { } other) {
                AddWeight(weights, Math.Min(points, other));
                AddWeight(weights, Math.Max(points, other));
            } else {
                AddWeight(weights, points);
            }
        }

        var bestPoints = 0;
        var bestWeight = -1;
        foreach (var (points, weight) in weights) {
            if (weight > bestWeight || (weight == bestWeight && points > bestPoints)) {
                bestWeight = weight;
                bestPoints = points;
            }
        }
        return bestPoints;
    }

    private static void AddWeight(List<KeyValuePair<int, int>> weights, int points) {
        var index = weights.FindIndex(x => x.Key == points);
        if (index < 0) {
            weights.Add(new(points, 1));
        } else {
            weights[index] = new(points, weights[index].Value + 1);
        }
    }
}
