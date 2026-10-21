using Retroscope.Importer.Source;
using Retroscope.Importer.Storage;
using Retroscope.Importer.Tests.Infrastructure;
using static Retroscope.Importer.Tests.EndToEnd.EndToEndImport;

namespace Retroscope.Importer.Tests.EndToEnd;

[Trait("Category", "EndToEnd")]
[Collection(ImporterPostgres.Name)]
public sealed class StorageTests(ImporterDatabases databases) {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Avatars_are_attached_from_the_cache_and_every_object_is_in_the_manifest() {
        var import = await CreateAsync(databases);

        var report = await import.FetchAndRunAsync();

        Assert.Equal(StorageStage.AvatarBlobId(new Guid(Alice)), await import.ScalarAsync<Guid>($"select avatar_blob_id from users where id = '{Alice}'"));
        Assert.Equal(StorageStage.AvatarBlobId(new Guid(Bob)), await import.ScalarAsync<Guid>($"select avatar_blob_id from users where id = '{Bob}'"));
        Assert.Equal(DBNull.Value, await import.ScalarAsync<object>($"select avatar_blob_id from users where id = '{Carol}'"));
        Assert.Equal("avatar image/png 10 " + Alice, await import.ScalarAsync<string>(
            $"select concat_ws(' ', purpose, content_type, byte_size, created_by) from blobs where id = '{StorageStage.AvatarBlobId(new Guid(Alice))}'"));
        Assert.Equal([
            $"avatars {Alice}.png imported {StorageStage.AvatarBlobId(new Guid(Alice))} hashed",
            $"avatars {Carol}.png skipped - hashed",
            "avatars shared/logo.png manifest_only - -",
            "chat-images session/picture.png manifest_only - -",
        ], (await RowsAsync(import.Target, """
            select concat_ws(' ', bucket, path, disposition, coalesce(blob_id::text, '-'), case when sha256 is null then '-' else 'hashed' end)
            from archive.storage_objects order by bucket, path
            """)).Select(x => (string)x[0]!));
        Assert.Equal("not_allowed", Assert.Single(report.Findings("skippedAvatars"))!["reason"]!.GetValue<string>());
        var tally = report.Table(StorageStage.ObjectsTable);
        Assert.Equal((4, 1, 2, 1), (tally.Source, tally.Imported, tally.Archived, tally.Skipped["not_profile_avatar"]));
    }

    [Fact]
    public async Task Storage_resumes_after_a_kill_and_the_run_attaches_what_was_staged() {
        var import = await CreateAsync(databases);
        var killed = new FakeFetcher(failAfter: 1);

        await Assert.ThrowsAnyAsync<HttpRequestException>(() => import.FetchStorageAsync(fetcher: killed));
        var stagedBeforeResume = await import.ScalarAsync<long>("select count(*) from archive.storage_cache");
        var resumed = new FakeFetcher();
        var result = await import.FetchStorageAsync(fetcher: resumed);
        await import.Runner().RunAsync(new RunOptions(), Ct);

        Assert.Equal(1L, stagedBeforeResume);
        Assert.Equal((1, 2), (result.AlreadyCached, result.Fetched));
        Assert.DoesNotContain(killed.Requested.Single(), resumed.Requested);
        Assert.Equal(2L, await import.ScalarAsync<long>("select count(*) from users where avatar_blob_id is not null"));
        Assert.Empty(await import.VerifyAsync());
    }

    [Fact]
    public async Task Without_staging_the_run_imports_no_avatar_and_says_why() {
        var import = await CreateAsync(databases);

        var report = await import.Runner().RunAsync(new RunOptions(), Ct);

        Assert.Equal(0L, await import.ScalarAsync<long>("select count(*) from blobs where purpose = 'avatar'"));
        Assert.Equal(2, report.Counter("storage.avatars.skipped.not_staged"));
        Assert.Equal("skipped", await import.ScalarAsync<string>($"select disposition from archive.storage_objects where path = '{Alice}.png'"));
        Assert.Equal(1, report.Table(StorageStage.ObjectsTable).Skipped["not_staged"]);
    }

    [Theory]
    [InlineData("https://project.supabase.example/storage/v1/object/public/avatars/" + Alice + ".png?t=5", "avatars", Alice + ".png")]
    [InlineData("http://localhost:8000/storage/v1/object/public/avatars/" + Alice + ".jpg", "avatars", Alice + ".jpg")]
    [InlineData("https://lh3.googleusercontent.com/a/photo=s96", "google", "https://lh3.googleusercontent.com/a/photo=s96")]
    [InlineData("https://project.supabase.example/storage/v1/object/public/avatars/" + Bob + ".png", null, null)]
    [InlineData("https://project.supabase.example/storage/v1/object/public/chat-images/" + Alice + ".png", null, null)]
    [InlineData("https://project.supabase.example/storage/v1/object/public/avatars/nested/" + Alice + ".png", null, null)]
    [InlineData("https://evil.example/" + Alice + ".png", null, null)]
    [InlineData("not a url", null, null)]
    public void Only_the_users_own_object_or_a_google_url_is_an_avatar(string url, string? bucket, string? path) {
        var key = StorageStage.AvatarKey(new Guid(Alice), url);

        Assert.Equal(bucket, key?.Bucket);
        Assert.Equal(path, key?.Path);
    }
}
