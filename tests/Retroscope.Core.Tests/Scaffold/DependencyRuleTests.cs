using System.Xml.Linq;

namespace Retroscope.Core.Tests.Scaffold;

// DESIGN §a dependency rule: Core is pure, Server adds Npgsql, Importer adds Npgsql and AngleSharp.
[Trait("Category", "Scaffold")]
public sealed class DependencyRuleTests {
    [Theory]
    [InlineData("src/Retroscope.Core/Retroscope.Core.csproj", new[] { "BCrypt.Net-Next" }, new string[0])]
    [InlineData("src/Retroscope.Server/Retroscope.Server.csproj", new[] { "Npgsql" }, new[] { "Retroscope.Core" })]
    [InlineData(
        "src/Retroscope.Importer/Retroscope.Importer.csproj",
        new[] { "AngleSharp", "Npgsql" },
        new[] { "Retroscope.Core" })]
    public void Project_references_only_what_the_design_allows(
        string projectPath,
        string[] packages,
        string[] projects) {
        var project = XDocument.Load(Path.Combine(RepoRoot.Path, projectPath));

        var actualPackages = Includes(project, "PackageReference").Order(StringComparer.Ordinal);
        var actualProjects = Includes(project, "ProjectReference")
            .Select(Path.GetFileNameWithoutExtension)
            .Order(StringComparer.Ordinal);

        Assert.Equal(packages, actualPackages);
        Assert.Equal(projects, actualProjects);
    }

    [Fact]
    public void Core_has_no_framework_references() {
        var project = XDocument.Load(Path.Combine(RepoRoot.Path, "src/Retroscope.Core/Retroscope.Core.csproj"));

        Assert.Empty(Includes(project, "FrameworkReference"));
        Assert.Equal("Microsoft.NET.Sdk", project.Root!.Attribute("Sdk")!.Value);
    }

    private static IEnumerable<string> Includes(XDocument project, string itemName) {
        return project.Descendants(itemName).Select(x => x.Attribute("Include")!.Value);
    }
}
