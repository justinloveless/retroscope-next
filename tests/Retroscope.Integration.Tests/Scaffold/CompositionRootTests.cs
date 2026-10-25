using System.Reflection;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Retroscope.Server.Hosting;

namespace Retroscope.Integration.Tests.Scaffold;

// Program.cs is frozen after U0 (DESIGN §a parallel-work rule), so it must already call every module hook
// and chain every JSON context.
[Trait("Category", "Scaffold")]
public sealed class CompositionRootTests {
    private static readonly Assembly Server = typeof(HostingModule).Assembly;
    private static readonly string ProgramSource = File.ReadAllText(
        Path.Combine(RepoRoot.Path, "src/Retroscope.Server/Program.cs"));

    public static TheoryData<string> Modules() {
        var data = new TheoryData<string>();
        foreach (var type in Server.GetTypes().Where(IsModule).OrderBy(x => x.Name, StringComparer.Ordinal)) {
            data.Add(type.Name);
        }
        return data;
    }

    public static TheoryData<string> JsonContexts() {
        var data = new TheoryData<string>();
        var contexts = Server.GetTypes()
            .Where(x => x.IsSubclassOf(typeof(JsonSerializerContext)))
            .OrderBy(x => x.Name, StringComparer.Ordinal);
        foreach (var type in contexts) {
            data.Add(type.Name);
        }
        return data;
    }

    [Fact]
    public void Server_declares_the_expected_number_of_modules() {
        Assert.Equal(23, Modules().Count);
    }

    [Theory]
    [MemberData(nameof(Modules))]
    public void Program_calls_both_hooks_of_every_module(string module) {
        Assert.Contains($"{module}.AddServices(services, configuration);", ProgramSource, StringComparison.Ordinal);
        Assert.Contains($"{module}.Map(app);", ProgramSource, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(JsonContexts))]
    public void Program_chains_every_json_context(string context) {
        Assert.Contains($"{context}.Default);", ProgramSource, StringComparison.Ordinal);
    }

    [Fact]
    public void Pipeline_hook_runs_after_services_and_before_routes() {
        var lastAddServices = ProgramSource.LastIndexOf(".AddServices(", StringComparison.Ordinal);
        var usePipeline = ProgramSource.IndexOf("HostingModule.UsePipeline(app);", StringComparison.Ordinal);
        var firstMap = ProgramSource.IndexOf(".Map(app);", StringComparison.Ordinal);

        Assert.InRange(usePipeline, lastAddServices + 1, firstMap - 1);
    }

    private static bool IsModule(Type type) {
        var addServices = type.GetMethod(
            "AddServices",
            BindingFlags.Public | BindingFlags.Static,
            [typeof(IServiceCollection), typeof(IConfiguration)]);
        return addServices != null;
    }
}
