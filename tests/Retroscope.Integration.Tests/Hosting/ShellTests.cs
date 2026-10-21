using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Retroscope.Server.Hosting;

namespace Retroscope.Integration.Tests.Hosting;

[Trait("Category", "Hosting")]
public sealed partial class ShellTests : IDisposable {
    private const string Hostile = "</script><script>alert(1)</script><!--";
    private readonly FakeWebBuild _web = new();

    public void Dispose() => _web.Dispose();

    [Fact]
    public async Task Boot_json_escapes_angle_brackets_so_it_cannot_close_its_script_element() {
        await using var app = await StartAsync(new EchoContributor(Hostile));

        var html = await GetHtmlAsync(app, "/retro/abc123");

        Assert.DoesNotContain(Hostile, html, StringComparison.Ordinal);
        var boot = BootElement(html);
        Assert.DoesNotContain('<', boot);
        Assert.DoesNotContain('>', boot);
        Assert.Contains("\\u003C/script\\u003E", boot, StringComparison.Ordinal);
        using var document = JsonDocument.Parse(boot);
        Assert.Equal(Hostile, document.RootElement.GetProperty("echo").GetProperty("text").GetString());
    }

    [Fact]
    public async Task Boot_json_holds_build_theme_and_contributed_members_and_the_route_parameters() {
        await using var app = await StartAsync(new EchoContributor("hi") { Theme = "dark" });

        var html = await GetHtmlAsync(app, "/teams/t-1/poker/Ab%20C");

        using var boot = JsonDocument.Parse(BootElement(html));
        var root = boot.RootElement;
        Assert.Equal(["build", "theme", "echo"], root.EnumerateObject().Select(x => x.Name));
        Assert.Matches("^[0-9a-f]{12}$", root.GetProperty("build").GetString());
        Assert.Equal("dark", root.GetProperty("theme").GetString());
        var echo = root.GetProperty("echo");
        Assert.Equal("/teams/:tid/poker/:code", echo.GetProperty("route").GetString());
        Assert.Equal("t-1", echo.GetProperty("tid").GetString());
        Assert.Equal("Ab C", echo.GetProperty("code").GetString());
    }

    [Fact]
    public async Task Theme_defaults_to_system() {
        await using var app = await StartAsync();

        using var boot = JsonDocument.Parse(BootElement(await GetHtmlAsync(app, "/")));

        Assert.Equal("system", boot.RootElement.GetProperty("theme").GetString());
    }

    [Fact]
    public async Task Shell_is_not_cached_and_is_html() {
        await using var app = await StartAsync();

        using var response = await app.Client.GetAsync(new Uri("/login", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html; charset=utf-8", response.Content.Headers.ContentType?.ToString());
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    [Fact]
    public async Task Poker_shell_links_route_css_preloads_its_chunks_and_the_pixel_font() {
        await using var app = await StartAsync();

        var html = await GetHtmlAsync(app, "/poker/ABCDEFGHJK");

        var expected = string.Join('\n',
            $"<link rel=\"stylesheet\" href=\"{FakeWebBuild.Kit}\">",
            $"<link rel=\"stylesheet\" href=\"{FakeWebBuild.Neotro}\">",
            $"<link rel=\"preload\" href=\"{FakeWebBuild.Font}\" as=\"font\" type=\"font/woff2\" crossorigin>",
            $"<link rel=\"modulepreload\" href=\"{FakeWebBuild.Shared}\">",
            $"<link rel=\"modulepreload\" href=\"{FakeWebBuild.PokerChunk}\">",
            "<script type=\"application/json\" id=\"boot\">");
        Assert.Contains(expected, html, StringComparison.Ordinal);
        Assert.Contains($"<script type=\"module\" src=\"{FakeWebBuild.Entry}\"></script>", html, StringComparison.Ordinal);
        Assert.Contains("<div id=\"app\"></div>", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Other_shells_skip_the_font_and_route_css() {
        await using var app = await StartAsync();

        var html = await GetHtmlAsync(app, "/dashboard");

        Assert.DoesNotContain(FakeWebBuild.Font, html, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeWebBuild.Neotro, html, StringComparison.Ordinal);
        Assert.Contains($"<link rel=\"modulepreload\" href=\"{FakeWebBuild.LandingChunk}\">", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Theme_script_follows_the_boot_element_and_precedes_the_entry() {
        await using var app = await StartAsync();

        var html = await GetHtmlAsync(app, "/");

        var boot = html.IndexOf("id=\"boot\"", StringComparison.Ordinal);
        var theme = html.IndexOf(FakeWebBuild.ThemeScript, StringComparison.Ordinal);
        var entry = html.IndexOf("type=\"module\"", StringComparison.Ordinal);
        Assert.True(boot >= 0 && boot < theme && theme < entry, html);
        Assert.True(html.Length < 3 * 1024, $"shell is {html.Length} bytes");
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/auth")]
    [InlineData("/auth/magic")]
    [InlineData("/auth/oidc/done")]
    [InlineData("/teams/abc")]
    [InlineData("/teams/abc/settings")]
    [InlineData("/invite")]
    [InlineData("/admin")]
    [InlineData("/admin/users/1")]
    [InlineData("/mentions")]
    [InlineData("/account")]
    public async Task Every_front_end_route_serves_the_shell(string path) {
        await using var app = await StartAsync();

        using var response = await app.Client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
    }

    [Fact]
    public async Task Api_boot_returns_the_same_document_for_in_app_navigation() {
        await using var app = await StartAsync(new EchoContributor(Hostile));

        using var response = await app.Client.GetAsync(
            new Uri("/api/boot?path=" + Uri.EscapeDataString("/teams/x/poker/Ab%20C?round=3"), UriKind.Relative),
            TestContext.Current.CancellationToken);
        var json = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
        Assert.DoesNotContain('<', json);
        using var boot = JsonDocument.Parse(json);
        var echo = boot.RootElement.GetProperty("echo");
        Assert.Equal("/teams/:tid/poker/:code", echo.GetProperty("route").GetString());
        Assert.Equal("Ab C", echo.GetProperty("code").GetString());
    }

    [Theory]
    [InlineData("/api/boot?path=/nowhere", 404, "not_found")]
    [InlineData("/api/boot?path=/poker/%25zz", 404, "not_found")]
    [InlineData("/api/boot?path=poker", 400, "invalid")]
    [InlineData("/api/boot", 400, "invalid")]
    public async Task Api_boot_refuses_unknown_and_malformed_paths(string url, int status, string code) {
        await using var app = await StartAsync();

        using var response = await app.Client.GetAsync(new Uri(url, UriKind.Relative), TestContext.Current.CancellationToken);

        await ProblemTests.AssertProblemAsync(response, status, code);
    }

    [Fact]
    public async Task Shell_without_a_web_build_still_renders() {
        await using var app = await TestApp.StartAsync();

        var html = await GetHtmlAsync(app, "/");

        Assert.Contains("<script type=\"application/json\" id=\"boot\">", html, StringComparison.Ordinal);
        Assert.DoesNotContain("type=\"module\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("rel=\"icon\"", html, StringComparison.Ordinal);
        Assert.DoesNotContain("rel=\"manifest\"", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Shell_links_the_favicon_the_svg_icon_and_the_web_manifest() {
        await using var app = await StartAsync();

        var html = await GetHtmlAsync(app, "/dashboard");

        Assert.Contains($"<meta name=\"theme-color\" content=\"{Shell.ThemeColor}\">", html, StringComparison.Ordinal);
        Assert.Contains("<link rel=\"icon\" href=\"/favicon.ico\" sizes=\"48x48\">", html, StringComparison.Ordinal);
        Assert.Contains($"<link rel=\"icon\" href=\"{FakeWebBuild.Logo}\" type=\"image/svg+xml\">", html, StringComparison.Ordinal);
        Assert.Contains("<link rel=\"manifest\" href=\"/manifest.webmanifest\">", html, StringComparison.Ordinal);
    }

    private Task<TestApp> StartAsync(IBootContributor? contributor = null) =>
        TestApp.StartAsync(_web.Settings(), services => {
            if (contributor is not null) {
                services.AddSingleton(contributor);
            }
        });

    private static async Task<string> GetHtmlAsync(TestApp app, string path) {
        using var response = await app.Client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        return await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
    }

    private static string BootElement(string html) {
        var match = Boot().Match(html);
        Assert.True(match.Success, html);
        return match.Groups[1].Value;
    }

    [GeneratedRegex("""<script type="application/json" id="boot">(.*?)</script>""", RegexOptions.Singleline)]
    private static partial Regex Boot();

    private sealed class EchoContributor(string text) : IBootContributor {
        public string? Theme { get; init; }

        public ValueTask ContributeAsync(BootContext boot, CancellationToken cancellationToken) {
            if (Theme is not null) {
                boot.Theme = Theme;
            }
            var echo = new Dictionary<string, string>(boot.Parameters) {
                ["text"] = text,
                ["route"] = boot.Route.Pattern,
            };
            boot.Set("echo", JsonSerializer.SerializeToElement(echo));
            return ValueTask.CompletedTask;
        }
    }
}
