using System.Net;
using System.Net.Http.Headers;
using Retroscope.Core.Ids;
using Retroscope.Core.Room;
using Retroscope.Integration.Tests.Auth;
using Retroscope.Integration.Tests.Blobs;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Auth;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Endpoints;

namespace Retroscope.Integration.Tests.AuthCore;

// PUT and DELETE /api/me/avatar (§d): raw image bytes up to 256 KiB, typed by their magic bytes.
[Trait("Category", "AuthCore")]
[Collection(SharedPostgres.Name)]
public sealed class AvatarTests(PostgresFixture postgres) {
    [Fact]
    public async Task An_uploaded_image_becomes_the_avatar_everyone_sees() {
        await using var host = await AuthHost.StartAsync(postgres);
        var (user, cookie) = await SignInAsync(host);
        var browser = host.Browser();
        browser.SetCookie(Sessions.CookieName, cookie);
        await browser.GetAsync("/api/me");

        using var response = await PutAsync(host, cookie, ImageFixtures.Png, "application/octet-stream");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        var avatar = await host.ScalarAsync<Guid>("select avatar_blob_id from users where id = $1", user.Value);
        Assert.Equal("image/png", await host.ScalarAsync<string>(
            "select content_type from blobs where id = $1 and purpose = 'avatar' and created_by = $2", avatar, user.Value));
        var me = await (await browser.GetAsync("/api/me")).JsonAsync();
        Assert.Equal(avatar, me.GetProperty("principal").GetProperty("avatar").GetGuid());
    }

    [Fact]
    public async Task A_new_avatar_deletes_the_one_it_replaces() {
        await using var host = await AuthHost.StartAsync(postgres);
        var (user, cookie) = await SignInAsync(host);
        await PutAsync(host, cookie, ImageFixtures.Png);
        var first = await host.ScalarAsync<Guid>("select avatar_blob_id from users where id = $1", user.Value);

        using var response = await PutAsync(host, cookie, ImageFixtures.WebpLossless);

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.NotEqual(first, await host.ScalarAsync<Guid>("select avatar_blob_id from users where id = $1", user.Value));
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from blobs"));
        Assert.Equal("image/webp", await host.ScalarAsync<string>("select content_type from blobs"));
    }

    [Theory]
    [InlineData("text/plain")]
    [InlineData("image/png")]
    public async Task Something_that_is_not_an_image_is_refused_whatever_its_content_type(string contentType) {
        await using var host = await AuthHost.StartAsync(postgres);
        var (user, cookie) = await SignInAsync(host);

        using var response = await PutAsync(host, cookie, "<svg xmlns='http://www.w3.org/2000/svg'/>"u8.ToArray(), contentType);

        Assert.Equal(HttpStatusCode.UnsupportedMediaType, response.StatusCode);
        Assert.Equal("unsupported_media_type", await response.ProblemCodeAsync());
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from blobs"));
        Assert.True(await host.ScalarAsync<bool>("select avatar_blob_id is null from users where id = $1", user.Value));
    }

    [Fact]
    public async Task The_limit_is_256_KiB() {
        await using var host = await AuthHost.StartAsync(postgres);
        var (_, cookie) = await SignInAsync(host);

        using var largest = await PutAsync(host, cookie, ImageFixtures.PngOfSize(BlobsRepo.AvatarMaxBytes));
        using var tooLarge = await PutAsync(host, cookie, ImageFixtures.PngOfSize(BlobsRepo.AvatarMaxBytes + 1));

        Assert.Equal(HttpStatusCode.NoContent, largest.StatusCode);
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLarge.StatusCode);
        Assert.Equal("too_large", await tooLarge.ProblemCodeAsync());
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from blobs"));
    }

    [Fact]
    public async Task Removing_the_avatar_deletes_its_blob() {
        await using var host = await AuthHost.StartAsync(postgres);
        var (user, cookie) = await SignInAsync(host);
        await PutAsync(host, cookie, ImageFixtures.Png);
        var browser = host.Browser();
        browser.SetCookie(Sessions.CookieName, cookie);

        using var response = await browser.DeleteAsync("/api/me/avatar");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.True(await host.ScalarAsync<bool>("select avatar_blob_id is null from users where id = $1", user.Value));
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from blobs"));
        var me = await (await browser.GetAsync("/api/me")).JsonAsync();
        Assert.False(me.GetProperty("principal").TryGetProperty("avatar", out var avatar) && avatar.ValueKind != System.Text.Json.JsonValueKind.Null);
    }

    [Fact]
    public async Task Only_signed_in_users_have_avatars() {
        await using var host = await AuthHost.StartAsync(postgres);

        using var response = await PutAsync(host, cookie: null, ImageFixtures.Png);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from blobs"));
    }

    // The blob and the pointer to it commit together: a failure after the insert leaves no blob nobody points at.
    [Fact]
    public async Task An_upload_that_fails_to_set_the_avatar_leaves_no_blob() {
        await using var host = await AuthHost.StartAsync(postgres);
        var (user, cookie) = await SignInAsync(host);
        await host.ExecuteAsync("""
            create function refuse_avatar() returns trigger language plpgsql as $$
            begin raise exception 'avatar refused'; end $$;
            create trigger refuse_avatar before update of avatar_blob_id on users for each row execute function refuse_avatar();
            """);

        using var response = await PutAsync(host, cookie, ImageFixtures.Png);

        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from blobs"));
        Assert.True(await host.ScalarAsync<bool>("select avatar_blob_id is null from users where id = $1", user.Value));
    }

    [Fact]
    public async Task An_avatar_for_a_principal_without_a_user_row_stores_nothing() {
        await using var host = await AuthHost.StartAsync(postgres);
        var guest = Guid.NewGuid();
        await host.ExecuteAsync("insert into principals (id, kind, display_name) values ($1, 'guest', 'Gus')", guest);

        var stored = await host.Service<UsersRepo>().SetAvatarAsync(
            new PrincipalId(guest), Image(ImageFixtures.Png), onlyIfNone: false, DateTimeOffset.UtcNow, AuthHost.Token);

        Assert.False(stored);
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from blobs"));
    }

    [Fact]
    public async Task A_profile_picture_never_replaces_an_avatar_and_leaves_no_blob() {
        await using var host = await AuthHost.StartAsync(postgres);
        var (user, cookie) = await SignInAsync(host);
        await PutAsync(host, cookie, ImageFixtures.Png);
        var avatar = await host.ScalarAsync<Guid>("select avatar_blob_id from users where id = $1", user.Value);

        var stored = await host.Service<UsersRepo>().SetAvatarAsync(user, Image(ImageFixtures.WebpLossless), onlyIfNone: true, DateTimeOffset.UtcNow, AuthHost.Token);

        Assert.False(stored);
        Assert.Equal(avatar, await host.ScalarAsync<Guid>("select avatar_blob_id from users where id = $1", user.Value));
        Assert.Equal(1L, await host.ScalarAsync<long>("select count(*) from blobs"));
    }

    private static SniffedImage Image(byte[] bytes) {
        Assert.True(SniffedImage.TryCreate(bytes, out var image));
        return image;
    }

    private static async Task<(PrincipalId User, string Cookie)> SignInAsync(AuthHost host) {
        var user = await host.CreateUserAsync("ada@example.com", verified: true);
        return (user, await host.CreateSessionAsync(user, AuthMethod.Password));
    }

    private static async Task<HttpResponseMessage> PutAsync(AuthHost host, string? cookie, byte[] body, string contentType = "image/webp") {
        using var request = new HttpRequestMessage(HttpMethod.Put, "/api/me/avatar") { Content = new ByteArrayContent(body) };
        request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
        request.Headers.Add(AuthHost.PeerHeader, AuthHost.DefaultPeer);
        request.Headers.Add("X-RS", "1");
        request.Headers.Add("Origin", AuthHost.PublicUrl);
        if (cookie is not null) {
            request.Headers.Add("Cookie", $"{Sessions.CookieName}={cookie}");
        }
        return await host.Client.SendAsync(request, AuthHost.Token);
    }
}
