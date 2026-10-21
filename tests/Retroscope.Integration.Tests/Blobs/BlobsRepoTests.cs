using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Endpoints;
using static Retroscope.Integration.Tests.Blobs.ImageFixtures;

namespace Retroscope.Integration.Tests.Blobs;

[Trait("Category", "Blobs")]
[Collection(SharedPostgres.Name)]
public sealed class BlobsRepoTests(PostgresFixture postgres) {
    private static readonly TimeSpan Old = BlobsRepo.UnattachedTtl + TimeSpan.FromHours(1);

    [Fact]
    public async Task Gc_deletes_only_unattached_board_images_older_than_the_ttl() {
        await using var app = await BlobApp.StartAsync(postgres);
        var user = await app.InsertUserAsync();
        var board = await app.InsertBoardAsync();
        var stale = await app.InsertBoardImageRowAsync(board, 10, Old);
        var attached = await app.InsertBoardImageRowAsync(board, 10, Old, attached: true);
        var young = await app.InsertBoardImageRowAsync(board, 10, TimeSpan.FromHours(23));
        var avatar = new BlobId(Guid.NewGuid());
        Assert.True(SniffedImage.TryCreate(Png, out var image));
        await app.Repo.InsertAvatarAsync(avatar, new PrincipalId(user), image, TestContext.Current.CancellationToken);
        await app.ExecuteAsync("update blobs set created_at = now() - interval '30 days' where id = $1", avatar.Value);

        var deleted = await app.Repo.DeleteUnattachedAsync(
            DateTimeOffset.UtcNow - BlobsRepo.UnattachedTtl,
            100,
            TestContext.Current.CancellationToken);

        Assert.Equal(1, deleted);
        var remaining = await IdsAsync(app);
        Assert.Equal(3, remaining.Count);
        Assert.Contains(attached, remaining);
        Assert.Contains(young, remaining);
        Assert.Contains(avatar.Value, remaining);
        Assert.DoesNotContain(stale, remaining);
    }

    [Fact]
    public async Task Gc_deletes_the_oldest_first_and_at_most_the_limit() {
        await using var app = await BlobApp.StartAsync(postgres);
        var board = await app.InsertBoardAsync();
        var oldest = await app.InsertBoardImageRowAsync(board, 10, Old + TimeSpan.FromHours(3));
        var older = await app.InsertBoardImageRowAsync(board, 10, Old + TimeSpan.FromHours(2));
        var old = await app.InsertBoardImageRowAsync(board, 10, Old + TimeSpan.FromHours(1));

        var deleted = await app.Repo.DeleteUnattachedAsync(
            DateTimeOffset.UtcNow - BlobsRepo.UnattachedTtl,
            2,
            TestContext.Current.CancellationToken);

        Assert.Equal(2, deleted);
        var remaining = await IdsAsync(app);
        Assert.Equal([old], remaining);
        Assert.DoesNotContain(oldest, remaining);
        Assert.DoesNotContain(older, remaining);
    }

    [Fact]
    public async Task Gc_uses_the_partial_index_on_unattached_board_images() {
        await using var app = await BlobApp.StartAsync(postgres);
        await using var connection = new NpgsqlConnection(app.Database.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using (var off = new NpgsqlCommand("set enable_seqscan = off", connection)) {
            await off.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }
        await using var explain = new NpgsqlCommand("explain " + BlobsRepo.DeleteUnattachedSql, connection) {
            Parameters = {
                new NpgsqlParameter<DateTimeOffset> { TypedValue = DateTimeOffset.UtcNow - BlobsRepo.UnattachedTtl },
                new NpgsqlParameter<int> { TypedValue = 100 },
            },
        };
        var plan = string.Join('\n', await ReadAllAsync(explain));

        Assert.Contains("blobs_unattached_ix", plan, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Avatars_are_capped_at_256_KiB() {
        await using var app = await BlobApp.StartAsync(postgres);
        var user = new PrincipalId(await app.InsertUserAsync());
        Assert.True(SniffedImage.TryCreate(PngOfSize(BlobsRepo.AvatarMaxBytes), out var largest));
        Assert.True(SniffedImage.TryCreate(PngOfSize(BlobsRepo.AvatarMaxBytes + 1), out var tooLarge));

        await app.Repo.InsertAvatarAsync(new BlobId(Guid.NewGuid()), user, largest, TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            app.Repo.InsertAvatarAsync(new BlobId(Guid.NewGuid()), user, tooLarge, TestContext.Current.CancellationToken));
        Assert.Equal(1L, await app.ScalarAsync<long>("select count(*) from blobs where purpose = 'avatar'"));
    }

    private static async Task<List<Guid>> IdsAsync(BlobApp app) {
        await using var connection = new NpgsqlConnection(app.Database.OwnerConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = new NpgsqlCommand("select id from blobs", connection);
        return [.. (await ReadAllAsync(command)).Select(Guid.Parse)];
    }

    private static async Task<List<string>> ReadAllAsync(NpgsqlCommand command) {
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(TestContext.Current.CancellationToken);
        while (await reader.ReadAsync(TestContext.Current.CancellationToken)) {
            values.Add(reader.GetValue(0).ToString()!);
        }
        return values;
    }
}
