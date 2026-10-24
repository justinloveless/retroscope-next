using Retroscope.Integration.Tests.Scaffold;
using Retroscope.Server.Data;

namespace Retroscope.Integration.Tests.Schema;

// tests/schema.snapshot.sql is the reviewable shape of the migrated database, grants included.
// Regenerate it with RS_UPDATE_SCHEMA_SNAPSHOT=1 after an intended change to a migration or the grants.
[Trait("Category", "Schema")]
[Collection(SharedPostgres.Name)]
public sealed class SchemaSnapshotTests(PostgresFixture postgres) {
    private static readonly string SnapshotPath = Path.Combine(RepoRoot.Path, "tests/schema.snapshot.sql");

    // Passing the migrator migrates a new database rather than cloning the fixture's template.
    [Fact]
    public async Task Migrated_schema_equals_the_committed_snapshot() {
        var database = await postgres.CreateDatabaseAsync(Migrator.FromEmbeddedScripts());

        var dump = await postgres.DumpSchemaAsync(database);

        if (Environment.GetEnvironmentVariable("RS_UPDATE_SCHEMA_SNAPSHOT") == "1") {
            await File.WriteAllTextAsync(SnapshotPath, dump, TestContext.Current.CancellationToken);
        }
        Assert.Equal(await File.ReadAllTextAsync(SnapshotPath, TestContext.Current.CancellationToken), dump);
    }
}
