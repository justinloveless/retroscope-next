using System.Globalization;
using System.Text.Json;
using Retroscope.Soak;

// Soak and chaos (DESIGN §j) against deploy/compose.test.yml (1 vCPU / 1 GB for our slice) behind Toxiproxy.
//   dotnet run --project tests/Retroscope.Soak -- --profile nightly --minutes 30
//   dotnet run --project tests/Retroscope.Soak -- --profile short --minutes 4      (while working on the soak)
//   dotnet run --project tests/Retroscope.Soak -- --self-test                       (the oracle's teeth, no Docker)
// The stack runs as compose project retroscope-soak on https://localhost:38443 (--port moves it and the next three
// ports) and is removed at the end unless --keep-stack. The report and the client's raw results go to
// artifacts/soak/<time>/.

var profile = Profile.Nightly;
TimeSpan? minutes = null;
var port = 38443;
var keepStack = false;
var selfTestOnly = false;
for (var i = 0; i < args.Length; i++) {
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value");
    switch (args[i]) {
        case "--profile":
            profile = Profile.Named(Next()) ?? throw new ArgumentException("--profile is nightly or short");
            break;
        case "--minutes":
            minutes = TimeSpan.FromMinutes(double.Parse(Next(), CultureInfo.InvariantCulture));
            break;
        case "--port":
            port = int.Parse(Next(), CultureInfo.InvariantCulture);
            break;
        case "--keep-stack":
            keepStack = true;
            break;
        case "--self-test":
            selfTestOnly = true;
            break;
        default:
            Console.Error.WriteLine("usage: Retroscope.Soak [--profile nightly|short] [--minutes M] [--port P] [--keep-stack] [--self-test]");
            return 2;
    }
}

var teeth = SelfTest.Run();
foreach (var failure in teeth) {
    Console.Error.WriteLine($"self-test: {failure}");
}
Console.WriteLine(teeth.Count == 0 ? "self-test: every planted bug was caught" : "self-test FAILED");
if (selfTestOnly || teeth.Count > 0) {
    return teeth.Count == 0 ? 0 : 1;
}

var root = FindRoot();
var output = Path.Combine(root, "artifacts/soak", DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture));
Directory.CreateDirectory(output);
var duration = minutes ?? (profile == Profile.Nightly ? TimeSpan.FromMinutes(30) : TimeSpan.FromMinutes(4));
var stack = new Stack(root, port);
Report report;
try {
    report = await new Run(profile, duration, stack, root, output).ExecuteAsync();
} finally {
    if (!keepStack) {
        await stack.DownAsync();
    }
}

var path = Path.Combine(output, "report.json");
await File.WriteAllTextAsync(path, report.Document.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine(report.Document["measured"]!.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
foreach (var finding in report.Findings.Take(40)) {
    Console.WriteLine($"  {finding.Gate}: {finding.Detail}");
}
foreach (var warning in report.Warnings.Take(20)) {
    Console.WriteLine($"  warning {warning.Gate}: {warning.Detail}");
}
Console.WriteLine($"gates: {report.Document["gates"]!.ToJsonString()}");
Console.WriteLine(report.Passed ? $"soak passed ({path})" : $"soak FAILED with {report.Findings.Count} findings ({path})");
return report.Passed ? 0 : 1;

static string FindRoot() {
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent) {
        if (File.Exists(Path.Combine(dir.FullName, "Retroscope.slnx"))) {
            return dir.FullName;
        }
    }
    throw new InvalidOperationException("run from inside the repository");
}
