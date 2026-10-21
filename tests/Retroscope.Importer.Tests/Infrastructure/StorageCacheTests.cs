using System.Collections.Concurrent;
using Retroscope.Importer.Archive;

namespace Retroscope.Importer.Tests.Infrastructure;

[Trait("Category", "Source")]
[Collection(ImporterPostgres.Name)]
public sealed class StorageCacheTests(ImporterDatabases databases) {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];

    [Fact]
    public async Task Staging_resumes_after_a_kill_without_fetching_cached_objects_again() {
        var harness = await ImportHarness.CreateAsync(databases);
        await SeedAvatarsAsync(harness, users: 8);
        var killed = new FakeFetcher(failAfter: 3);

        await Assert.ThrowsAnyAsync<Exception>(() => harness.Runner().FetchStorageAsync(killed, concurrency: 1, cancellationToken: Ct));
        var cachedAfterKill = await CachedPathsAsync(harness);
        var resumed = new FakeFetcher();
        var result = await harness.Runner().FetchStorageAsync(resumed, cancellationToken: Ct);

        Assert.Equal(3, cachedAfterKill.Count);
        Assert.Equal(3, result.AlreadyCached);
        Assert.Equal(6, result.Fetched);
        Assert.Empty(resumed.Requested.Select(x => x.AbsolutePath).Intersect(killed.Requested.Take(3).Select(x => x.AbsolutePath)));
        Assert.Equal(9, (await CachedPathsAsync(harness)).Count);
        Assert.Equal(32, await harness.TargetScalarAsync<int>("select min(octet_length(sha256)) from archive.storage_cache"));
    }

    [Fact]
    public async Task Only_imported_users_own_avatars_and_allow_listed_google_urls_are_fetched() {
        var harness = await ImportHarness.CreateAsync(databases);
        await SeedAvatarsAsync(harness, users: 2);
        await harness.SourceSqlAsync("""
            insert into storage.buckets (id, name, public) values ('chat-images', 'chat-images', true) on conflict do nothing;
            insert into storage.objects (bucket_id, name, metadata) values
              ('avatars', '00000000-0000-0000-0000-00000000ffff.png', '{"size": 10}'),
              ('avatars', 'shared/logo.png', '{"size": 10}'),
              ('chat-images', 'u0/pic.png', '{"size": 10}');
            update public.profiles set avatar_url = 'https://evil.example/a.png' where id = '00000000-0000-0000-0000-000000000001';
            """);
        var fetcher = new FakeFetcher();

        await harness.Runner().FetchStorageAsync(fetcher, cancellationToken: Ct);

        Assert.Equal([
            "/a/photo",
            "/storage/v1/object/public/avatars/00000000-0000-0000-0000-000000000000.png",
            "/storage/v1/object/public/avatars/00000000-0000-0000-0000-000000000001.png",
        ], fetcher.Requested.Select(x => x.AbsolutePath).Order(StringComparer.Ordinal));
        Assert.Equal([
            ("avatars", "00000000-0000-0000-0000-000000000000.png"),
            ("avatars", "00000000-0000-0000-0000-000000000001.png"),
            ("google", "https://lh3.googleusercontent.com/a/photo"),
        ],
            await CachedPathsAsync(harness));
    }

    [Fact]
    public async Task Non_images_and_oversized_objects_are_skipped() {
        var harness = await ImportHarness.CreateAsync(databases);
        await SeedAvatarsAsync(harness, users: 2, google: false);
        var fetcher = new FakeFetcher(bodyFor: x => x.AbsolutePath.Contains("000000000000.png", StringComparison.Ordinal) ? "<html>"u8.ToArray() : null);

        var result = await harness.Runner().FetchStorageAsync(fetcher, cancellationToken: Ct);

        Assert.Equal(["larger than 256 KiB", "not PNG, JPEG or WebP"], result.Skipped.Select(x => x.Reason).Order(StringComparer.Ordinal));
        Assert.Empty(await CachedPathsAsync(harness));
    }

    [Theory]
    [InlineData("https://lh3.googleusercontent.com/a/x", true)]
    [InlineData("https://lh6.googleusercontent.com/a/x", true)]
    [InlineData("http://lh3.googleusercontent.com/a/x", false)]
    [InlineData("https://lh3.googleusercontent.com:8443/a/x", false)]
    [InlineData("https://user@lh3.googleusercontent.com/a/x", false)]
    [InlineData("https://lh3.googleusercontent.com.evil.example/a/x", false)]
    [InlineData("https://googleusercontent.com/a/x", false)]
    [InlineData(null, false)]
    public void Google_avatar_hosts_are_allow_listed(string? url, bool allowed) =>
        Assert.Equal(allowed, StorageCache.IsGoogleAvatar(url, out _));

    [Fact]
    public void Images_are_recognized_by_magic_bytes() {
        Assert.Equal("image/png", ImageSniffer.Detect(Png));
        Assert.Equal("image/jpeg", ImageSniffer.Detect([0xFF, 0xD8, 0xFF, 0xE0]));
        Assert.Equal("image/webp", ImageSniffer.Detect("RIFF\0\0\0\0WEBPVP8 "u8));
        Assert.Null(ImageSniffer.Detect("GIF89a"u8));
        Assert.Null(ImageSniffer.Detect([]));
    }

    // users × avatars/<id>.png, plus one Google avatar on the first profile.
    private static Task SeedAvatarsAsync(ImportHarness harness, int users, bool google = true) => harness.SourceSqlAsync($"""
        insert into auth.users (id, email)
          select ('00000000-0000-0000-0000-' || lpad(to_hex(i), 12, '0'))::uuid, 'user' || i || '@example.com'
          from generate_series(0, {users - 1}) i;
        insert into public.profiles (id, role, avatar_url)
          select id, 'user', case when {(google ? "true" : "false")} and email = 'user0@example.com'
                                  then 'https://lh3.googleusercontent.com/a/photo' end
          from auth.users;
        insert into storage.buckets (id, name, public) values ('avatars', 'avatars', true) on conflict do nothing;
        insert into storage.objects (bucket_id, name, metadata)
          select 'avatars', id || '.png', '{"{"}"size": 11{"}"}' from auth.users;
        """);

    private static async Task<List<(string, string)>> CachedPathsAsync(ImportHarness harness) {
        var rows = await harness.TargetScalarAsync<object>(
            "select string_agg(bucket || chr(9) || path, chr(10) order by bucket, path) from archive.storage_cache");
        return rows is string text ? [.. text.Split('\n').Select(x => x.Split('\t')).Select(x => (x[0], x[1]))] : [];
    }

    private sealed class FakeFetcher(int failAfter = int.MaxValue, Func<Uri, byte[]?>? bodyFor = null) : IObjectFetcher {
        private readonly ConcurrentQueue<Uri> _requested = new();
        private int _calls;

        public IReadOnlyList<Uri> Requested => [.. _requested];

        public Task<byte[]?> FetchAsync(Uri url, int maxBytes, CancellationToken cancellationToken) {
            if (Interlocked.Increment(ref _calls) > failAfter) {
                throw new HttpRequestException("killed");
            }
            _requested.Enqueue(url);
            return Task.FromResult(bodyFor is null ? Png : bodyFor(url));
        }
    }
}
