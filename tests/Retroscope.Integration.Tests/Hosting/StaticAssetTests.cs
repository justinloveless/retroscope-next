using System.Net;
using Retroscope.Server.Hosting;

namespace Retroscope.Integration.Tests.Hosting;

[Trait("Category", "Hosting")]
public sealed class StaticAssetTests : IDisposable {
    private readonly FakeWebBuild _web = new();

    public void Dispose() => _web.Dispose();

    [Theory]
    [InlineData("br, gzip", "br", "BR:entry")]
    [InlineData("gzip, deflate, br, zstd", "br", "BR:entry")]
    [InlineData("gzip", "gzip", "GZ:entry")]
    [InlineData("br;q=0, gzip", "gzip", "GZ:entry")]
    [InlineData("*", "br", "BR:entry")]
    [InlineData("*;q=0, gzip", "gzip", "GZ:entry")]
    [InlineData("identity", null, "console.log('entry');")]
    [InlineData(null, null, "console.log('entry');")]
    public async Task Precompressed_siblings_are_negotiated_br_then_gzip_then_identity(
        string? acceptEncoding,
        string? expectedEncoding,
        string expectedBody) {
        await using var app = await TestApp.StartAsync(_web.Settings());

        using var response = await GetAsync(app, FakeWebBuild.Entry, acceptEncoding);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expectedEncoding, response.Content.Headers.ContentEncoding.SingleOrDefault());
        Assert.Equal(expectedBody, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal(expectedBody.Length, response.Content.Headers.ContentLength);
        Assert.Equal("text/javascript; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(StaticAssets.CacheControl, response.Headers.CacheControl?.ToString());
        Assert.Equal("Accept-Encoding", string.Join(",", response.Headers.Vary));
    }

    [Fact]
    public async Task A_file_with_only_gzip_falls_back_to_gzip_for_brotli_clients() {
        await using var app = await TestApp.StartAsync(_web.Settings());

        using var response = await GetAsync(app, FakeWebBuild.Shared, "br, gzip");

        Assert.Equal("gzip", response.Content.Headers.ContentEncoding.Single());
        Assert.Equal("GZ:shared", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_file_without_siblings_is_sent_raw_without_vary() {
        await using var app = await TestApp.StartAsync(_web.Settings());

        using var response = await GetAsync(app, FakeWebBuild.Font, "br, gzip");

        Assert.Empty(response.Content.Headers.ContentEncoding);
        Assert.Empty(response.Headers.Vary);
        Assert.Equal("font/woff2", response.Content.Headers.ContentType?.ToString());
        Assert.Equal(FakeWebBuild.ETagOf(FakeWebBuild.Font), response.Headers.ETag?.ToString());
    }

    [Theory]
    [InlineData("br", "\"etag-entry-AAAA.br\"")]
    [InlineData("gzip", "\"etag-entry-AAAA.gz\"")]
    [InlineData(null, "\"etag-entry-AAAA\"")]
    public async Task Each_representation_has_its_own_strong_etag(string? acceptEncoding, string etag) {
        await using var app = await TestApp.StartAsync(_web.Settings());

        using var response = await GetAsync(app, FakeWebBuild.Entry, acceptEncoding);

        Assert.Equal(etag, response.Headers.ETag?.ToString());
    }

    [Theory]
    [InlineData("\"etag-entry-AAAA.br\"")]
    [InlineData("W/\"etag-entry-AAAA.br\"")]
    [InlineData("\"other\", \"etag-entry-AAAA.br\"")]
    [InlineData("*")]
    public async Task A_matching_if_none_match_is_a_304_without_body(string ifNoneMatch) {
        await using var app = await TestApp.StartAsync(_web.Settings());

        using var response = await GetAsync(app, FakeWebBuild.Entry, "br", ifNoneMatch);

        Assert.Equal(HttpStatusCode.NotModified, response.StatusCode);
        Assert.Empty(await response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
        Assert.Equal("\"etag-entry-AAAA.br\"", response.Headers.ETag?.ToString());
        Assert.Equal(StaticAssets.CacheControl, response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Another_representations_etag_does_not_match() {
        await using var app = await TestApp.StartAsync(_web.Settings());

        using var response = await GetAsync(app, FakeWebBuild.Entry, "gzip", "\"etag-entry-AAAA.br\"");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("GZ:entry", await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Head_sends_headers_only() {
        await using var app = await TestApp.StartAsync(_web.Settings());
        using var request = new HttpRequestMessage(HttpMethod.Head, FakeWebBuild.Kit);
        request.Headers.Add("Accept-Encoding", "br");

        using var response = await app.Client.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("br", response.Content.Headers.ContentEncoding.Single());
        Assert.Equal(6, response.Content.Headers.ContentLength);
        Assert.Equal("text/css; charset=utf-8", response.Content.Headers.ContentType?.ToString());
    }

    [Theory]
    [InlineData(FakeWebBuild.OnDiskOnly)]
    [InlineData("/assets/../manifest.json")]
    [InlineData("/assets/entry-AAAA.js.br")]
    [InlineData("/assets/missing.js")]
    [InlineData("/assets")]
    [InlineData("/assets/")]
    public async Task Only_files_listed_in_the_manifest_are_served(string path) {
        await using var app = await TestApp.StartAsync(_web.Settings());

        using var response = await GetAsync(app, path, null);

        await ProblemTests.AssertProblemAsync(response, 404, "not_found");
    }

    [Fact]
    public async Task Unsafe_methods_on_assets_are_refused() {
        await using var app = await TestApp.StartAsync(_web.Settings());
        using var request = CsrfTests.Unsafe(HttpMethod.Post, FakeWebBuild.Entry);

        using var response = await app.Client.SendAsync(request, TestContext.Current.CancellationToken);

        await ProblemTests.AssertProblemAsync(response, 405, "method_not_allowed");
    }

    [Fact]
    public async Task A_manifest_entry_outside_assets_fails_startup() {
        var manifest = Path.Combine(_web.Root, "manifest.json");
        File.WriteAllText(manifest, File.ReadAllText(manifest).Replace("/assets/kit-EEEE.css\":{", "/kit.css\":{", StringComparison.Ordinal));

        await Assert.ThrowsAsync<InvalidOperationException>(async () => {
            await using var app = await TestApp.StartAsync(_web.Settings());
        });
    }

    [Theory]
    [InlineData(FakeWebBuild.Favicon, "image/x-icon", "ICO-bytes")]
    [InlineData(FakeWebBuild.Robots, "text/plain; charset=utf-8", "User-agent: *")]
    [InlineData(FakeWebBuild.WebManifest, "application/manifest+json", "{\"name\":\"Retroscope\"}")]
    public async Task Root_files_are_served_with_their_type_and_a_one_day_cache(string path, string contentType, string body) {
        await using var app = await TestApp.StartAsync(_web.Settings());

        using var response = await GetAsync(app, path, null);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(contentType, response.Content.Headers.ContentType?.ToString());
        Assert.Equal(StaticAssets.RootCacheControl, response.Headers.CacheControl?.ToString());
        Assert.Equal(FakeWebBuild.ETagOf(path), response.Headers.ETag?.ToString());
        Assert.Equal(body, await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Root_files_are_negotiated_and_revalidated_like_assets() {
        await using var app = await TestApp.StartAsync(_web.Settings());

        using var compressed = await GetAsync(app, FakeWebBuild.Robots, "br, gzip");
        using var revalidated = await GetAsync(app, FakeWebBuild.Robots, "br", "\"etag-robots.br\"");

        Assert.Equal("br", compressed.Content.Headers.ContentEncoding.Single());
        Assert.Equal("BR:robots", await compressed.Content.ReadAsStringAsync(TestContext.Current.CancellationToken));
        Assert.Equal("Accept-Encoding", string.Join(",", compressed.Headers.Vary));
        Assert.Equal(HttpStatusCode.NotModified, revalidated.StatusCode);
        Assert.Equal(StaticAssets.RootCacheControl, revalidated.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Only_the_known_root_names_are_served_from_the_public_list() {
        await using var app = await TestApp.StartAsync(_web.Settings());

        using var response = await GetAsync(app, FakeWebBuild.UnlistedRoot, null);

        await ProblemTests.AssertProblemAsync(response, 404, "not_found");
    }

    [Fact]
    public async Task Root_files_are_missing_without_a_web_build() {
        await using var app = await TestApp.StartAsync();

        using var response = await GetAsync(app, FakeWebBuild.Favicon, null);

        await ProblemTests.AssertProblemAsync(response, 404, "not_found");
    }

    [Fact]
    public async Task Unsafe_methods_on_root_files_are_refused() {
        await using var app = await TestApp.StartAsync(_web.Settings());
        using var request = CsrfTests.Unsafe(HttpMethod.Post, FakeWebBuild.Robots);

        using var response = await app.Client.SendAsync(request, TestContext.Current.CancellationToken);

        await ProblemTests.AssertProblemAsync(response, 405, "method_not_allowed");
    }

    private static async Task<HttpResponseMessage> GetAsync(TestApp app, string path, string? acceptEncoding, string? ifNoneMatch = null) {
        using var request = new HttpRequestMessage(HttpMethod.Get, path);
        if (acceptEncoding is not null) {
            request.Headers.TryAddWithoutValidation("Accept-Encoding", acceptEncoding);
        }
        if (ifNoneMatch is not null) {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }
        return await app.Client.SendAsync(request, TestContext.Current.CancellationToken);
    }
}
