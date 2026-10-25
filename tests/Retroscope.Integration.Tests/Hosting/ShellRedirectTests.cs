using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Retroscope.Server.Hosting;

namespace Retroscope.Integration.Tests.Hosting;

// UX18 decisions 1, 6 and 8: a page decides its redirects before any boot contributor runs, and every redirect is a
// no-store 302, because where it leads depends on who asks.
[Trait("Category", "Hosting")]
public sealed class ShellRedirectTests {
    [Theory]
    [InlineData("/account")]
    [InlineData("/mentions")]
    [InlineData("/teams")]
    [InlineData("/teams/abc?tab=retros")]
    [InlineData("/teams/abc/settings")]
    [InlineData("/teams/abc/poker/ABCDEFGHJK")]
    [InlineData("/poker/ABCDEFGHJK")]
    [InlineData("/retro/ABCDEFGHJK?card=1&x=a%20b")]
    [InlineData("/admin/users/1")]
    public async Task A_signed_out_visitor_of_a_member_page_is_sent_to_sign_in_before_any_contributor_runs(string path) {
        var contributor = new CountingContributor();
        await using var app = await TestApp.StartAsync(services: services => services.AddSingleton<IBootContributor>(contributor));

        using var response = await app.Client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        AssertRedirect(response, "/login?return=" + Uri.EscapeDataString(path));
        Assert.Equal(0, contributor.Calls);
    }

    [Fact]
    public async Task A_legacy_guest_is_a_signed_out_visitor() {
        var contributor = new CountingContributor();
        await using var app = await TestApp.StartAsync(services: services => services.AddSingleton<IBootContributor>(contributor));
        using var request = TestApp.Get("/retro/ABCDEFGHJK");
        request.Headers.Add(TestApp.CallerHeader, "guest");

        using var response = await app.Client.SendAsync(request, TestContext.Current.CancellationToken);

        AssertRedirect(response, "/login?return=%2Fretro%2FABCDEFGHJK");
        Assert.Equal(0, contributor.Calls);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/login?return=%2Faccount")]
    [InlineData("/auth/magic")]
    [InlineData("/auth/verify")]
    [InlineData("/invite")]
    public async Task The_welcome_sign_in_and_invite_pages_open_to_anyone(string path) {
        var contributor = new CountingContributor();
        await using var app = await TestApp.StartAsync(services: services => services.AddSingleton<IBootContributor>(contributor));

        using var response = await app.Client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1, contributor.Calls);
    }

    [Fact]
    public async Task An_overlong_return_path_is_dropped() {
        await using var app = await TestApp.StartAsync();
        var path = "/retro/ABCDEFGHJK?q=" + new string('a', 3000);

        using var response = await app.Client.GetAsync(new Uri(path, UriKind.Relative), TestContext.Current.CancellationToken);

        AssertRedirect(response, "/login");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("user")]
    public async Task The_old_dashboard_path_redirects_everyone_to_the_root(string? caller) {
        await using var app = await TestApp.StartAsync();
        using var request = TestApp.Get("/dashboard?from=pwa");
        if (caller is not null) {
            request.Headers.Add(TestApp.CallerHeader, caller);
        }

        using var response = await app.Client.SendAsync(request, TestContext.Current.CancellationToken);

        AssertRedirect(response, "/");
    }

    [Fact]
    public async Task The_first_redirect_that_answers_a_signed_in_caller_wins_and_the_page_is_not_built() {
        var contributor = new CountingContributor();
        await using var app = await TestApp.StartAsync(services: services => {
            services.AddSingleton<IBootContributor>(contributor);
            services.AddSingleton<IShellRedirect>(new FixedRedirect("/account", null));
            services.AddSingleton<IShellRedirect>(new FixedRedirect("/account", "/teams/first"));
            services.AddSingleton<IShellRedirect>(new FixedRedirect("/account", "/teams/second"));
        });
        using var account = TestApp.Get("/account");
        account.Headers.Add(TestApp.CallerHeader, "user");
        using var mentions = TestApp.Get("/mentions");
        mentions.Headers.Add(TestApp.CallerHeader, "user");

        using var redirected = await app.Client.SendAsync(account, TestContext.Current.CancellationToken);
        Assert.Equal(0, contributor.Calls);
        using var served = await app.Client.SendAsync(mentions, TestContext.Current.CancellationToken);

        AssertRedirect(redirected, "/teams/first");
        Assert.Equal(HttpStatusCode.OK, served.StatusCode);
        Assert.Equal(1, contributor.Calls);
    }

    [Fact]
    public async Task Redirects_are_not_asked_about_a_signed_out_visitor() {
        await using var app = await TestApp.StartAsync(services: services =>
            services.AddSingleton<IShellRedirect>(new FixedRedirect("/", "/teams/first")));

        using var response = await app.Client.GetAsync(new Uri("/", UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Api_boot_never_redirects() {
        var contributor = new CountingContributor();
        await using var app = await TestApp.StartAsync(services: services => services.AddSingleton<IBootContributor>(contributor));

        using var response = await app.Client.GetAsync(
            new Uri("/api/boot?path=" + Uri.EscapeDataString("/account"), UriKind.Relative), TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(1, contributor.Calls);
    }

    private static void AssertRedirect(HttpResponseMessage response, string location) {
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(location, response.Headers.Location?.OriginalString);
        Assert.Equal("no-store", response.Headers.CacheControl?.ToString());
    }

    private sealed class CountingContributor : IBootContributor {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public ValueTask ContributeAsync(BootContext boot, CancellationToken cancellationToken) {
            Interlocked.Increment(ref _calls);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class FixedRedirect(string pattern, string? location) : IShellRedirect {
        public ValueTask<string?> RedirectAsync(ShellRoute route, Caller caller, CancellationToken cancellationToken) =>
            ValueTask.FromResult(route.Pattern == pattern ? location : null);
    }
}
