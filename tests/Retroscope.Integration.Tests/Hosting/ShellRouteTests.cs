using Retroscope.Server.Hosting;

namespace Retroscope.Integration.Tests.Hosting;

// Cases follow core/router.js match(): '*' matches the rest (also nothing), ':name' needs a non-empty segment and
// is percent-decoded, and a malformed escape leaves the route unmatched.
[Trait("Category", "Hosting")]
public sealed class ShellRouteTests {
    [Theory]
    [InlineData("/", "/", "")]
    [InlineData("/login", "/login", "")]
    [InlineData("/auth", "/auth/*", "")]
    [InlineData("/auth/", "/auth/*", "")]
    [InlineData("/auth/magic/x", "/auth/*", "")]
    [InlineData("/teams/abc", "/teams/:id", "id=abc")]
    [InlineData("/teams/abc/settings", "/teams/:id/settings", "id=abc")]
    [InlineData("/teams/t1/poker/AbC", "/teams/:tid/poker/:code", "tid=t1;code=AbC")]
    [InlineData("/poker/a%20b", "/poker/:code", "code=a b")]
    [InlineData("/poker/%E2%9C%93", "/poker/:code", "code=✓")]
    [InlineData("/retro/x-1", "/retro/:code", "code=x-1")]
    [InlineData("/admin", "/admin/*", "")]
    [InlineData("/admin/users", "/admin/*", "")]
    public void Paths_resolve_like_the_client_router(string path, string pattern, string parameters) {
        var resolved = ShellRoute.Resolve(path);

        Assert.NotNull(resolved);
        Assert.Equal(pattern, resolved.Value.Route.Pattern);
        Assert.Equal(parameters, string.Join(';', resolved.Value.Parameters.Select(x => $"{x.Key}={x.Value}")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("/nope")]
    [InlineData("/login/")]
    [InlineData("/poker/")]
    [InlineData("/poker/a/b")]
    [InlineData("/poker/%zz")]
    [InlineData("/poker/%E2%9C")]
    [InlineData("/poker/%")]
    [InlineData("/teams//settings")]
    public void Other_paths_do_not_resolve(string path) {
        Assert.Null(ShellRoute.Resolve(path));
    }
}
