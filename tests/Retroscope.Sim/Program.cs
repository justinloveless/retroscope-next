using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Retroscope.Sim;

// Deterministic simulation (DESIGN §j): `--seeds N` runs seeds 1..N (PR: 2000, nightly: 50000) and then proves the
// harness has teeth by planting a cursor bug and a missing precondition; `--seed S --trace` replays one seed.

var seeds = 2000;
var start = 1;
int? single = null;
var steps = 200;
var bug = InjectedBug.None;
var faults = Faults.Default;
var trace = false;
var teeth = true;
var strict = false;
var determinism = false;
for (var i = 0; i < args.Length; i++) {
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i]) {
        case "--seeds":
            seeds = int.Parse(Next(), CultureInfo.InvariantCulture);
            break;
        case "--start":
            start = int.Parse(Next(), CultureInfo.InvariantCulture);
            break;
        case "--seed":
            single = int.Parse(Next(), CultureInfo.InvariantCulture);
            break;
        case "--steps":
            steps = int.Parse(Next(), CultureInfo.InvariantCulture);
            break;
        case "--inject":
            bug = Enum.Parse<InjectedBug>(Next(), ignoreCase: true);
            break;
        case "--faults":
            faults = ParseFaults(Next());
            break;
        case "--trace":
            trace = true;
            break;
        case "--no-teeth":
            teeth = false;
            break;
        case "--strict":
            strict = true;
            break;
        case "--determinism":
            determinism = true;
            break;
        default:
            Console.Error.WriteLine("usage: Retroscope.Sim [--seeds N] [--start S] [--seed S [--trace]] [--steps K] [--inject cursor|precondition] [--faults default|all|<flag,flag>] [--no-teeth] [--strict] [--determinism]");
            return 2;
    }
}

if (single is { } seed) {
    var result = await new World(seed, new WorldOptions(steps, bug, faults, trace)).RunAsync();
    foreach (var line in result.Trace) {
        Console.WriteLine(line);
    }
    Report(result);
    var ok = !Failed(result, strict);
    Console.WriteLine(ok ? $"seed {seed}: ok" : $"seed {seed}: FAILED");
    return ok ? 0 : 1;
}

if (determinism) {
    // Every seed twice: a seed is only replayable if both runs end the same way.
    var options = new WorldOptions(steps, bug, faults, true);
    var differing = new ConcurrentBag<int>();
    await Parallel.ForEachAsync(Enumerable.Range(start, seeds), async (each, _) => {
        var first = await new World(each, options).RunAsync();
        var second = await new World(each, options).RunAsync();
        if (first.Digest != second.Digest) {
            differing.Add(each);
            // Epochs are random per actor incarnation (§c.3) and never steer a decision.
            static string Plain(string line) => System.Text.RegularExpressions.Regex.Replace(line, "e=[0-9a-f]{16}", "e=…");
            var at = Enumerable.Range(0, Math.Min(first.Trace.Count, second.Trace.Count)).FirstOrDefault(x => Plain(first.Trace[x]) != Plain(second.Trace[x]));
            var files = new[] { first, second }.Select((run, n) => {
                var path = Path.Combine(Path.GetTempPath(), $"retroscope-sim-{each}-{n + 1}.txt");
                File.WriteAllLines(path, run.Trace);
                return path;
            });
            Console.WriteLine($"seed {each} diverges at trace line {at} (traces: {string.Join(", ", files)})");
        }
    });
    Console.WriteLine(differing.IsEmpty
        ? $"determinism: {seeds} seeds replayed identically"
        : $"determinism: {differing.Count} seeds differ between runs: {string.Join(", ", differing.Order().Take(20))}");
    return differing.IsEmpty ? 0 : 1;
}

var watch = Stopwatch.StartNew();
var (hit, stats) = await RunAsync(Enumerable.Range(start, seeds), new WorldOptions(steps, bug, faults, false), strict, stopAtFirst: false);
var failures = hit.Where(x => Failed(x, strict)).ToList();
Console.WriteLine(
    $"sim: {seeds - failures.Count}/{seeds} seeds passed in {watch.Elapsed.TotalSeconds:0.0} s (faults: {faults}{(bug == InjectedBug.None ? "" : $", injected: {bug}")}); " +
    $"{stats.Commands} commands, {stats.Commits} commits, {stats.CommitFaults} commit faults, {stats.Crashes} crashes, {stats.Redeploys} redeploys, " +
    $"{stats.Drops} drops, {stats.StorageFaults} storage faults, {stats.FullQueueEpisodes} full-queue episodes, " +
    $"{stats.SpotlightLost} unsettled spotlight positions lost, {stats.AutoReveals} auto-reveals, {stats.CidReuseNacks} cid_reuse nacks; " +
    $"intents: {stats.Acked} acked, {stats.Nacked} nacked, {stats.Dropped} dropped by rule, {stats.Lost} lost to storage faults");
foreach (var issue in hit.SelectMany(x => x.Known.Keys).Distinct().Order(StringComparer.Ordinal)) {
    var hits = hit.Where(x => x.Known.ContainsKey(issue)).ToList();
    Console.WriteLine(
        $"known open issue {issue}: hit in {hits.Count} seeds (first: seed {hits.Min(x => x.Seed)}); " +
        $"see docs/decisions/U15.md{(strict ? "" : ", --strict fails on it")}");
}
foreach (var failure in failures.OrderBy(x => x.Seed).Take(12)) {
    Report(failure);
}
if (failures.Count > 12) {
    Console.WriteLine($"failing seeds: {string.Join(", ", failures.Select(x => x.Seed).Order().Take(200))}");
}
if (failures.Count > 0) {
    Console.WriteLine($"replay: dotnet run --project tests/Retroscope.Sim -- --seed {failures.Min(x => x.Seed)} --steps {steps} --trace{FaultArgs()}");
    return 1;
}
if (bug != InjectedBug.None || !teeth) {
    return 0;
}

// Teeth: each planted bug must make some seed fail that passes without it.
var caught = true;
foreach (var planted in new[] { InjectedBug.Cursor, InjectedBug.Precondition }) {
    var (unclean, _) = await RunAsync(Enumerable.Range(start, seeds), new WorldOptions(steps, planted, faults, false), strict: false, stopAtFirst: true);
    var found = unclean.Where(x => x.Violations.Count > 0).ToList();
    if (found.Count == 0) {
        caught = false;
        Console.WriteLine($"teeth: the injected {planted} bug was NOT caught in {seeds} seeds");
    } else {
        var first = found.MinBy(x => x.Seed)!;
        Console.WriteLine($"teeth: the injected {planted} bug is caught (seed {first.Seed}: {first.Violations[0].Split('\n')[0]})");
    }
}
return caught ? 0 : 1;

// Every result that hit something: a violation, or a known open issue.
static async Task<(List<WorldResult> Unclean, Stats Stats)> RunAsync(IEnumerable<int> seeds, WorldOptions options, bool strict, bool stopAtFirst) {
    var failures = new ConcurrentBag<WorldResult>();
    var total = new Stats();
    var gate = new Lock();
    using var stop = new CancellationTokenSource();
    try {
        await Parallel.ForEachAsync(seeds, new ParallelOptions { MaxDegreeOfParallelism = Environment.ProcessorCount, CancellationToken = stop.Token }, async (seed, _) => {
            var result = await new World(seed, options).RunAsync();
            lock (gate) {
                total.Add(result.Stats);
            }
            if (result.Violations.Count > 0 || result.Known.Count > 0) {
                failures.Add(result);
                if (stopAtFirst && Failed(result, strict)) {
                    await stop.CancelAsync();
                }
            }
        });
    } catch (OperationCanceledException) when (stopAtFirst) {
    }
    return ([.. failures], total);
}

static bool Failed(WorldResult result, bool strict) => result.Violations.Count > 0 || (strict && result.Known.Count > 0);

static void Report(WorldResult result) {
    if (result.Violations.Count == 0 && result.Known.Count == 0) {
        return;
    }
    Console.WriteLine($"seed {result.Seed}:");
    foreach (var violation in result.Violations) {
        Console.WriteLine($"  {violation}");
    }
    foreach (var (issue, count) in result.Known) {
        Console.WriteLine($"  known open issue {issue} ×{count}");
    }
}

static Faults ParseFaults(string text) {
    return text switch {
        "default" => Faults.Default,
        "all" => Faults.All,
        _ => text.Split(',').Aggregate(Faults.None, (all, name) => all | Enum.Parse<Faults>(name, ignoreCase: true)),
    };
}

string FaultArgs() => faults == Faults.Default ? "" : $" --faults {faults.ToString().Replace(" ", "", StringComparison.Ordinal)}";
