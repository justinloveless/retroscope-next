using Retroscope.Importer.Tests.Infrastructure;

namespace Retroscope.Importer.Tests.Identity;

/// <summary>
/// Golden rows per fixture: every target row the identity and team stages write, their archive rows and their part of
/// the report. Regenerate with <c>RS_UPDATE_GOLDEN=1</c> and review the diff.
/// </summary>
[Trait("Category", "Identity")]
[Collection(ImporterPostgres.Name)]
public sealed class IdentityGoldenTests(ImporterDatabases databases) {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("accounts")]
    [InlineData("teams")]
    [InlineData("invites")]
    public async Task Fixture_imports_to_the_golden_rows(string fixture) {
        var import = await IdentityImport.CreateAsync(databases, fixture);

        await import.RunAsync(Ct);
        var actual = await import.DumpAsync(Ct);

        var golden = IdentityImport.GoldenPath(fixture);
        if (Environment.GetEnvironmentVariable("RS_UPDATE_GOLDEN") == "1") {
            await File.WriteAllTextAsync(golden, actual, Ct);
        }
        Assert.Equal((await File.ReadAllTextAsync(golden, Ct)).ReplaceLineEndings("\n"), actual);
    }

    [Theory]
    [InlineData("accounts")]
    [InlineData("teams")]
    [InlineData("invites")]
    public async Task A_second_run_reproduces_the_same_rows(string fixture) {
        var import = await IdentityImport.CreateAsync(databases, fixture);

        await import.RunAsync(Ct);
        var first = await import.DumpAsync(Ct);
        await import.RunAsync(Ct);
        var second = await import.DumpAsync(Ct);

        Assert.Equal(first, second);
    }

    [Theory]
    [InlineData("accounts")]
    [InlineData("teams")]
    [InlineData("invites")]
    public async Task Every_source_row_is_imported_skipped_or_archived(string fixture) {
        var import = await IdentityImport.CreateAsync(databases, fixture);

        var report = await import.RunAsync(Ct);

        Assert.All(IdentityImport.SourceTables, table => {
            var tally = report.Table(table);
            Assert.True(tally.Source == tally.Imported + tally.Archived + tally.Skipped.Values.Sum(),
                $"{table}: source {tally.Source}, imported {tally.Imported}, archived {tally.Archived}, skipped {tally.Skipped.Values.Sum()}");
        });
    }
}
