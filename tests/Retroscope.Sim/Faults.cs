namespace Retroscope.Sim;

[Flags]
internal enum Faults {
    None = 0,
    Network = 1,
    Commits = 2,
    Crashes = 4,
    Storage = 8,
    FullQueues = 16,
    Membership = 32,
    Board = 64,

    /// <summary>sessionStorage goes back to an older copy as a whole.</summary>
    StaleStorage = 128,

    /// <summary>
    /// A duplicated tab whose original stays open and misses the boot check (the residual case of §c.6). Two live tabs
    /// then hold one outbox and one cid, and each can be answered truthfully for the same intent (see UF13 decisions).
    /// </summary>
    Twins = 256,

    /// <summary>The server notices a dropped socket only after the client reconnected (4409).</summary>
    StaleSocket = 512,

    Default = Network | Commits | Crashes | Storage | FullQueues | Membership | Board | StaleStorage | Twins | StaleSocket,
    All = Default,
}

internal sealed record WorldOptions(int Steps, InjectedBug Bug, Faults Faults, bool Trace);

/// <param name="Digest">A hash of how the run ended; two runs of one seed must agree (replayability).</param>
internal sealed record WorldResult(
    int Seed,
    string Digest,
    IReadOnlyList<string> Violations,
    IReadOnlyDictionary<string, int> Known,
    IReadOnlyList<string> Trace,
    Stats Stats);

internal sealed class Stats {
    public int Commands { get; set; }

    public int Commits { get; set; }

    public int Crashes { get; set; }

    public int Redeploys { get; set; }

    public int CommitFaults { get; set; }

    public int StorageFaults { get; set; }

    public int Drops { get; set; }

    public int FullQueueEpisodes { get; set; }

    public int AutoReveals { get; set; }

    public int TimerExpiries { get; set; }

    public int CidReuseNacks { get; set; }

    /// <summary>Twin intents lost to the residual case §c.6 accepts (see <see cref="Oracle.TwinResiduals"/>).</summary>
    public int TwinResiduals { get; set; }

    /// <summary>Crashes that lost a spotlight position which had not settled yet (§c.8).</summary>
    public int SpotlightLost { get; set; }

    public int Acked { get; set; }

    public int Nacked { get; set; }

    public int Dropped { get; set; }

    public int Lost { get; set; }

    public void Add(Stats other) {
        Commands += other.Commands;
        Commits += other.Commits;
        Crashes += other.Crashes;
        Redeploys += other.Redeploys;
        CommitFaults += other.CommitFaults;
        StorageFaults += other.StorageFaults;
        Drops += other.Drops;
        FullQueueEpisodes += other.FullQueueEpisodes;
        AutoReveals += other.AutoReveals;
        TimerExpiries += other.TimerExpiries;
        CidReuseNacks += other.CidReuseNacks;
        TwinResiduals += other.TwinResiduals;
        SpotlightLost += other.SpotlightLost;
        Acked += other.Acked;
        Nacked += other.Nacked;
        Dropped += other.Dropped;
        Lost += other.Lost;
    }
}
