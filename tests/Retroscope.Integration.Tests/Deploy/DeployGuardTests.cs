using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Retroscope.Integration.Tests.Scaffold;

namespace Retroscope.Integration.Tests.Deploy;

// U43's guards on what reaches the VPS (§i.1, §k): a frozen first migration, a compose file that only pulls pinned
// images, the 1 vCPU / 1 GB slice, and a test stack that runs the same database and limits as production.
[Trait("Category", "Deploy")]
public sealed partial class DeployGuardTests {
    // Frozen before the first production deploy. The migrator refuses to start against a database where this file
    // was applied with other bytes, so a schema change is always a new 0002+ file.
    private const string InitSha256 = "121cfd3ebba84b32fca04989c5e9bc84f65bee9dca3eee912d1d0b4f44614ed4";

    private const double SliceCpus = 1.0;
    private const int SliceMiB = 1024;

    private static readonly string[] Slice = ["app", "db", "backup"];

    [Fact]
    public void The_first_migration_is_frozen() {
        var bytes = File.ReadAllBytes(Path.Combine(RepoRoot.Path, "db/migrations/0001_init.sql"));

        Assert.Equal(InitSha256, Convert.ToHexStringLower(SHA256.HashData(bytes)));
    }

    [Fact]
    public void The_production_compose_never_builds_and_pins_every_image_by_digest() {
        var compose = Read("docker-compose.yml");

        Assert.DoesNotContain("build:", compose, StringComparison.Ordinal);
        var images = Image().Matches(compose).Select(x => x.Groups[1].Value).ToList();
        Assert.Equal(["app", "db", "backup", "importer"], Services(compose).Keys);
        Assert.Equal(4, images.Count);
        Assert.All(images, image => Assert.Matches(PinnedImage(), image));
    }

    [Fact]
    public void Our_slice_stays_within_one_vcpu_and_one_gigabyte_with_the_app_or_the_importer() {
        var services = Services(Read("docker-compose.yml"));

        foreach (var heavy in new[] { "app", "importer" }) {
            var members = new[] { heavy, "db", "backup" };
            Assert.True(members.Sum(x => Cpus(services[x])) <= SliceCpus, $"{string.Join('+', members)} exceed {SliceCpus} CPU");
            Assert.True(members.Sum(x => MemoryMiB(services[x])) <= SliceMiB, $"{string.Join('+', members)} exceed {SliceMiB} MiB");
        }
    }

    [Fact]
    public void The_test_stack_runs_the_production_database_settings_and_limits() {
        var production = Services(Read("docker-compose.yml"));
        var test = Services(Read("compose.test.yml"));

        Assert.Equal(Command(production["db"]), Command(test["db"]));
        foreach (var name in Slice) {
            Assert.Equal(Cpus(production[name]), Cpus(test[name]));
            Assert.Equal(MemoryMiB(production[name]), MemoryMiB(test[name]));
        }
        Assert.Equal(Image().Match(production["db"]).Groups[1].Value, PostgresAnchor().Match(Read("compose.test.yml")).Groups[1].Value);
    }

    private static string Read(string name) => File.ReadAllText(Path.Combine(RepoRoot.Path, "deploy", name));

    // Top-level services, each with the text of its block.
    private static Dictionary<string, string> Services(string compose) {
        var services = new Dictionary<string, string>(StringComparer.Ordinal);
        var lines = compose[compose.IndexOf("\nservices:\n", StringComparison.Ordinal)..].Split('\n').Skip(2);
        string? current = null;
        var block = new List<string>();
        foreach (var line in lines) {
            if (line.Length > 0 && !line.StartsWith(' ') && !line.StartsWith('#')) {
                break;
            }
            if (ServiceName().Match(line) is { Success: true } match) {
                if (current is not null) {
                    services[current] = string.Join('\n', block);
                }
                current = match.Groups[1].Value;
                block.Clear();
            } else {
                block.Add(line);
            }
        }
        services[current!] = string.Join('\n', block);
        return services;
    }

    private static double Cpus(string service) =>
        double.Parse(CpusLine().Match(service).Groups[1].Value, CultureInfo.InvariantCulture);

    private static int MemoryMiB(string service) =>
        int.Parse(MemoryLine().Match(service).Groups[1].Value, CultureInfo.InvariantCulture);

    private static string Command(string service) => CommandBlock().Match(service).Value;

    [GeneratedRegex(@"^  ([a-z][a-z0-9-]*):\s*$")]
    private static partial Regex ServiceName();

    [GeneratedRegex(@"^    image: (\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex Image();

    [GeneratedRegex(@"^[a-zA-Z0-9./:${}_-]+@sha256:(\$\{[A-Z_]+\}|[0-9a-f]{64})$")]
    private static partial Regex PinnedImage();

    [GeneratedRegex(@"^x-postgres-image: &postgres-image (\S+)\s*$", RegexOptions.Multiline)]
    private static partial Regex PostgresAnchor();

    [GeneratedRegex(@"^    cpus: ([0-9.]+)\s*$", RegexOptions.Multiline)]
    private static partial Regex CpusLine();

    [GeneratedRegex(@"^    mem_limit: ([0-9]+)m\s*$", RegexOptions.Multiline)]
    private static partial Regex MemoryLine();

    [GeneratedRegex(@"^    command: >\n(      .*\n)+", RegexOptions.Multiline)]
    private static partial Regex CommandBlock();
}
