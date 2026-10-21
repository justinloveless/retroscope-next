namespace Retroscope.Importer.Tests.Scaffold;

// DESIGN §h.5: the importer never references Retroscope.Server.
[Trait("Category", "Scaffold")]
public sealed class ImporterReferenceTests {
    [Fact]
    public void Importer_assembly_does_not_reference_the_server() {
        var importer = typeof(Program).Assembly;

        var references = importer.GetReferencedAssemblies().Select(x => x.Name);

        Assert.DoesNotContain("retroscope", references);
        Assert.Equal("retroscope-import", importer.GetName().Name);
    }
}
