using System.Net;
using System.Security.Cryptography;
using Microsoft.Extensions.Logging;
using Retroscope.Core.Ids;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Endpoints;
using static Retroscope.Integration.Tests.Blobs.ImageFixtures;

namespace Retroscope.Integration.Tests.Blobs;

[Trait("Category", "Blobs")]
[Collection(SharedPostgres.Name)]
public sealed class BlobEndpointTests(PostgresFixture postgres) {
    private const long MiB = 1024 * 1024;

    [Fact]
    public async Task An_upload_stores_the_sniffed_type_and_returns_the_id() {
        await using var app = await BlobApp.StartAsync(postgres);
        var guest = await app.InsertGuestAsync();
        var board = await app.InsertBoardAsync();

        var id = await app.UploadedIdAsync(board, Jpeg, $"guest:{guest}");

        Assert.Equal("image/jpeg", await app.ScalarAsync<string>("select content_type from blobs where id = $1", id));
        Assert.Equal(Jpeg.Length, await app.ScalarAsync<int>("select byte_size from blobs where id = $1", id));
        Assert.Equal(SHA256.HashData(Jpeg), await app.ScalarAsync<byte[]>("select sha256 from blobs where id = $1", id));
        Assert.Equal(board, await app.ScalarAsync<Guid>("select board_id from blobs where id = $1", id));
        Assert.Equal(guest, await app.ScalarAsync<Guid>("select created_by from blobs where id = $1", id));
        Assert.True(await app.ScalarAsync<bool>("select attached_at is null and purpose = 'board_image' from blobs where id = $1", id));
    }

    [Fact]
    public async Task The_declared_content_type_is_ignored() {
        await using var app = await BlobApp.StartAsync(postgres);
        var guest = await app.InsertGuestAsync();
        var board = await app.InsertBoardAsync();

        using var html = await app.UploadAsync(board, Append(Png, "<html>"), $"guest:{guest}", contentType: "image/png");
        var id = await app.UploadedIdAsync(board, WebpLossy, $"guest:{guest}");

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, html.StatusCode);
        Assert.Equal("image/webp", await app.ScalarAsync<string>("select content_type from blobs where id = $1", id));
    }

    [Theory]
    [MemberData(nameof(Polyglots))]
    public async Task Polyglots_and_non_images_are_refused(string name, byte[] body) {
        await using var app = await BlobApp.StartAsync(postgres);
        var guest = await app.InsertGuestAsync();
        var board = await app.InsertBoardAsync();

        using var response = await app.UploadAsync(board, body, $"guest:{guest}");

        Assert.True(HttpStatusCode.UnsupportedMediaType == response.StatusCode, name);
        Assert.Contains("unsupported_media_type", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(0L, await app.ScalarAsync<long>("select count(*) from blobs"));
    }

    public static TheoryData<string, byte[]> Polyglots => new() {
        { "png + html", Append(Png, "<html><script>alert(1)</script>") },
        { "gif + js", Append(Gif, "*/=alert(document.domain);//") },
        { "jpeg + zip", [.. Jpeg, .. "PK\u0003\u0004"u8, .. new byte[26]] },
        { "png signature + svg", [.. Png.AsSpan(0, 8), .. "<svg onload=alert(1)>"u8] },
        { "svg", "<svg xmlns=\"http://www.w3.org/2000/svg\"><script>alert(1)</script></svg>"u8.ToArray() },
        { "empty", [] },
    };

    [Fact]
    public async Task Board_images_are_capped_at_2_MiB() {
        await using var app = await BlobApp.StartAsync(postgres);
        var guest = await app.InsertGuestAsync();
        var board = await app.InsertBoardAsync();

        var largest = await app.UploadedIdAsync(board, PngOfSize(BlobsRepo.BoardImageMaxBytes), $"guest:{guest}");
        using var tooLarge = await app.UploadAsync(board, PngOfSize(BlobsRepo.BoardImageMaxBytes + 1), $"guest:{guest}");

        Assert.Equal(BlobsRepo.BoardImageMaxBytes, await app.ScalarAsync<int>("select byte_size from blobs where id = $1", largest));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Contains("too_large", await tooLarge.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_board_holds_at_most_50_MiB_of_images() {
        await using var app = await BlobApp.StartAsync(postgres);
        var guest = await app.InsertGuestAsync();
        var full = await app.InsertBoardAsync();
        var other = await app.InsertBoardAsync();
        for (var i = 0; i < 24; i++) {
            await app.InsertBoardImageRowAsync(full, (int)(2 * MiB));
        }
        await app.InsertBoardImageRowAsync(full, (int)(2 * MiB) - Png.Length);

        await app.UploadedIdAsync(full, Png, $"guest:{guest}");
        using var over = await app.UploadAsync(full, Png, $"guest:{guest}");
        await app.UploadedIdAsync(other, Png, $"guest:{guest}");

        Assert.Equal(BlobsRepo.BoardQuotaBytes, 50 * MiB);
        Assert.Equal(HttpStatusCode.Conflict, over.StatusCode);
        Assert.Contains("board_quota", await over.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Equal(50 * MiB, await app.ScalarAsync<long>("select sum(byte_size) from blobs where board_id = $1", full));
    }

    [Fact]
    public async Task The_global_ceiling_answers_507_and_logs_an_alert() {
        await using var app = await BlobApp.StartAsync(postgres, bytesMax: 2 * Png.Length);
        var guest = await app.InsertGuestAsync();
        var first = await app.InsertBoardAsync();
        var second = await app.InsertBoardAsync();

        await app.UploadedIdAsync(first, Png, $"guest:{guest}");
        await app.UploadedIdAsync(second, Png, $"guest:{guest}");
        using var full = await app.UploadAsync(second, Gif, $"guest:{guest}");

        Assert.Equal(HttpStatusCode.InsufficientStorage, full.StatusCode);
        Assert.Contains("storage_full", await full.Content.ReadAsStringAsync(TestContext.Current.CancellationToken), StringComparison.Ordinal);
        Assert.Contains(app.Logs.Entries, x => x.Level == LogLevel.Error && x.Message.Contains("BLOB_BYTES_MAX", StringComparison.Ordinal));
        Assert.Equal(2L * Png.Length, await app.Repo.BoardImageBytesAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Avatars_do_not_count_against_the_board_image_ceiling() {
        await using var app = await BlobApp.StartAsync(postgres, bytesMax: Png.Length);
        var user = await app.InsertUserAsync();
        var board = await app.InsertBoardAsync();
        Assert.True(SniffedImage.TryCreate(PngOfSize(BlobsRepo.AvatarMaxBytes), out var avatar));
        await app.Repo.InsertAvatarAsync(new BlobId(Guid.NewGuid()), new PrincipalId(user), avatar, TestContext.Current.CancellationToken);

        await app.UploadedIdAsync(board, Png, $"user:{user}");
    }

    [Fact]
    public async Task Uploads_are_limited_to_30_per_hour_per_principal() {
        await using var app = await BlobApp.StartAsync(postgres);
        var guest = await app.InsertGuestAsync();
        var board = await app.InsertBoardAsync();
        for (var i = 0; i < 30; i++) {
            await app.UploadedIdAsync(board, Gif, $"guest:{guest}");
        }

        using var limited = await app.UploadAsync(board, Gif, $"guest:{guest}", peer: "198.51.100.99");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.True(limited.Headers.RetryAfter?.Delta > TimeSpan.Zero);
    }

    [Fact]
    public async Task Uploads_are_limited_to_60_per_hour_per_ip_whoever_sends_them() {
        await using var app = await BlobApp.StartAsync(postgres);
        var board = await app.InsertBoardAsync();
        for (var i = 0; i < 60; i++) {
            var guest = await app.InsertGuestAsync();
            await app.UploadedIdAsync(board, Gif, $"guest:{guest}");
        }
        var fresh = await app.InsertGuestAsync();

        using var limited = await app.UploadAsync(board, Gif, $"guest:{fresh}");
        using var elsewhere = await app.UploadAsync(board, Gif, $"guest:{fresh}", peer: "203.0.113.200");

        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.Equal(HttpStatusCode.Created, elsewhere.StatusCode);
    }

    [Fact]
    public async Task Uploading_needs_a_principal_and_access_to_a_board_that_takes_content() {
        await using var app = await BlobApp.StartAsync(postgres);
        var member = await app.InsertUserAsync();
        var outsider = await app.InsertUserAsync();
        var team = await app.InsertTeamAsync(member);
        var teamBoard = await app.InsertBoardAsync("team", team);
        var archived = await app.InsertBoardAsync(archived: true);
        var voting = await app.InsertBoardAsync(stage: "voting", stagesEnabled: true);
        var discussing = await app.InsertBoardAsync(stage: "discussing", stagesEnabled: true);
        var discussingWithActions = await app.InsertBoardAsync(stage: "discussing", stagesEnabled: true);
        await app.InsertActionItemsColumnAsync(discussingWithActions);
        var votingWithoutStages = await app.InsertBoardAsync(stage: "voting", stagesEnabled: false);
        var deleted = await app.InsertBoardAsync(deleted: true);

        Assert.Equal("401 unauthenticated", await OutcomeAsync(app, teamBoard, caller: null));
        Assert.Equal("404 not_found", await OutcomeAsync(app, teamBoard, $"user:{outsider}"));
        Assert.Equal("404 not_found", await OutcomeAsync(app, Guid.CreateVersion7(), $"user:{member}"));
        Assert.Equal("404 not_found", await OutcomeAsync(app, deleted, $"user:{member}"));
        Assert.Equal("201", await OutcomeAsync(app, teamBoard, $"user:{member}"));
        Assert.Equal("403 board_archived", await OutcomeAsync(app, archived, $"user:{member}"));
        Assert.Equal("403 stage_forbids", await OutcomeAsync(app, voting, $"user:{member}"));
        Assert.Equal("403 stage_forbids", await OutcomeAsync(app, discussing, $"user:{member}"));
        Assert.Equal("201", await OutcomeAsync(app, discussingWithActions, $"user:{member}"));
        Assert.Equal("201", await OutcomeAsync(app, votingWithoutStages, $"user:{member}"));
    }

    [Fact]
    public async Task Board_images_are_served_with_the_blob_headers() {
        await using var app = await BlobApp.StartAsync(postgres);
        var guest = await app.InsertGuestAsync();
        var board = await app.InsertBoardAsync();
        var id = await app.UploadedIdAsync(board, Png, $"guest:{guest}");

        using var response = await app.GetAsync(id, $"guest:{guest}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(Png, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal("image/png", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(BlobEndpoints.BlobContentSecurityPolicy, Header(response, "Content-Security-Policy"));
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("same-origin", Header(response, "Cross-Origin-Resource-Policy"));
        Assert.Equal("private, max-age=31536000, immutable", Header(response, "Cache-Control"));
    }

    [Fact]
    public async Task Avatars_are_public() {
        await using var app = await BlobApp.StartAsync(postgres);
        var user = await app.InsertUserAsync();
        var id = new BlobId(Guid.NewGuid());
        Assert.True(SniffedImage.TryCreate(WebpLossless, out var avatar));
        await app.Repo.InsertAvatarAsync(id, new PrincipalId(user), avatar, TestContext.Current.CancellationToken);

        using var response = await app.GetAsync(id.Value, caller: null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(WebpLossless, await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal("image/webp", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(BlobEndpoints.BlobContentSecurityPolicy, Header(response, "Content-Security-Policy"));
        Assert.Equal("nosniff", Header(response, "X-Content-Type-Options"));
        Assert.Equal("same-origin", Header(response, "Cross-Origin-Resource-Policy"));
        Assert.Equal("public, max-age=31536000, immutable", Header(response, "Cache-Control"));
    }

    [Fact]
    public async Task Board_images_are_served_only_to_principals_with_board_access() {
        await using var app = await BlobApp.StartAsync(postgres);
        var member = await app.InsertUserAsync();
        var outsider = await app.InsertUserAsync();
        var holder = await app.InsertGuestAsync();
        var staleHolder = await app.InsertGuestAsync();
        var creator = await app.InsertUserAsync();
        var team = await app.InsertTeamAsync(member);
        var linkImage = await app.InsertBoardImageRowAsync(await app.InsertBoardAsync(), 1);
        var teamImage = await app.InsertBoardImageRowAsync(await app.InsertBoardAsync("team", team), 1);
        var passwordBoard = await app.InsertBoardAsync("password", createdBy: creator);
        var passwordImage = await app.InsertBoardImageRowAsync(passwordBoard, 1);
        await app.InsertGrantAsync(passwordBoard, holder, current: true);
        await app.InsertGrantAsync(passwordBoard, staleHolder, current: false);
        var teamPasswordImage = await app.InsertBoardImageRowAsync(await app.InsertBoardAsync("password", team), 1);
        var archivedImage = await app.InsertBoardImageRowAsync(await app.InsertBoardAsync(archived: true), 1);
        var deletedImage = await app.InsertBoardImageRowAsync(await app.InsertBoardAsync(deleted: true), 1);

        Assert.Equal(HttpStatusCode.OK, await StatusAsync(app, linkImage, $"guest:{holder}"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(app, linkImage, caller: null));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(app, teamImage, $"user:{member}"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(app, teamImage, $"user:{outsider}"));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(app, passwordImage, $"guest:{holder}"));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(app, passwordImage, $"user:{creator}"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(app, passwordImage, $"guest:{staleHolder}"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(app, passwordImage, $"user:{outsider}"));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(app, teamPasswordImage, $"user:{member}"));
        Assert.Equal(HttpStatusCode.OK, await StatusAsync(app, archivedImage, $"user:{outsider}"));
        Assert.Equal(HttpStatusCode.NotFound, await StatusAsync(app, deletedImage, $"user:{outsider}"));
    }

    [Fact]
    public async Task A_forbidden_board_image_looks_exactly_like_a_missing_blob() {
        await using var app = await BlobApp.StartAsync(postgres);
        var outsider = await app.InsertUserAsync();
        var team = await app.InsertTeamAsync();
        var hidden = await app.InsertBoardImageRowAsync(await app.InsertBoardAsync("team", team), 1);

        using var forbidden = await app.GetAsync(hidden, $"user:{outsider}");
        using var missing = await app.GetAsync(Guid.NewGuid(), $"user:{outsider}");

        Assert.Equal(HttpStatusCode.NotFound, forbidden.StatusCode);
        Assert.Equal(Describe(missing), Describe(forbidden));
        Assert.Equal(
            await missing.Content.ReadAsStringAsync(TestContext.Current.CancellationToken),
            await forbidden.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    private static async Task<string> OutcomeAsync(BlobApp app, Guid board, string? caller) {
        using var response = await app.UploadAsync(board, Png, caller);
        if (response.StatusCode == HttpStatusCode.Created) {
            return "201";
        }
        using var document = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        return $"{(int)response.StatusCode} {document.RootElement.GetProperty("code").GetString()}";
    }

    private static async Task<HttpStatusCode> StatusAsync(BlobApp app, Guid blob, string? caller) {
        using var response = await app.GetAsync(blob, caller);
        return response.StatusCode;
    }

    // Unparsed, so the header reads as the server wrote it.
    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.NonValidated.TryGetValues(name, out var values)
        || response.Content.Headers.NonValidated.TryGetValues(name, out values)
            ? string.Join(", ", values)
            : null;

    private static string Describe(HttpResponseMessage response) => string.Join('\n', response.Headers.Concat(response.Content.Headers)
        .Where(x => x.Key != "Date")
        .OrderBy(x => x.Key, StringComparer.Ordinal)
        .Select(x => $"{x.Key}: {string.Join(", ", x.Value)}"));
}
