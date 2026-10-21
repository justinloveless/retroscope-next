namespace Retroscope.Soak;

/// <summary>The load model and the chaos schedule of one run (DESIGN §j).</summary>
internal sealed record Profile(
    string Name,
    int PokerRooms,
    int RetroRooms,
    int SpotlightRooms,
    int ClientsPerRoom,
    double CommandsPerSecond,
    int StormBoards,
    int StormCards,
    int StormCreators,
    TimeSpan BurstEvery,
    TimeSpan KillEvery,
    TimeSpan PartitionLength) {
    /// <summary>
    /// 20 rooms × 8 clients × 0.3 commands/s, a burst every 2 min, 2 spotlight-driven rooms, a kill every 5 min with
    /// 50 boards × 200 cards loaded, 30 s partitions.
    /// </summary>
    public static readonly Profile Nightly = new(
        "nightly", 15, 5, 2, 8, 0.3, 50, 200, 10,
        TimeSpan.FromMinutes(2), TimeSpan.FromMinutes(5), TimeSpan.FromSeconds(30));

    /// <summary>The same shape, small and fast, for working on the soak itself.</summary>
    public static readonly Profile Short = new(
        "short", 3, 2, 1, 4, 0.3, 5, 40, 2,
        TimeSpan.FromSeconds(40), TimeSpan.FromSeconds(90), TimeSpan.FromSeconds(10));

    public static Profile? Named(string name) => name switch {
        "nightly" => Nightly,
        "short" => Short,
        _ => null,
    };
}
