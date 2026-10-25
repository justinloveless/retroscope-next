using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Retroscope.Integration.Tests.Scaffold;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Hosting;

namespace Retroscope.Integration.Tests.Hosting;

// §d/§j: every route of the real server declares a RoutePolicy and a RateLimit, and every room route is limited per IP
// and per principal (S7). Unsafe methods need no metadata: CSRF is a global middleware.
[Trait("Category", "Hosting")]
[Collection(SharedPostgres.Name)]
public sealed partial class RouteInventoryTests(PostgresFixture postgres) {
    [Fact]
    public async Task Every_route_declares_a_route_policy_and_a_rate_limit() {
        var endpoints = await EndpointsAsync();

        Assert.NotEmpty(endpoints);
        var missing = endpoints
            .Where(x => x.Metadata.GetMetadata<RoutePolicyMetadata>() is null || x.Metadata.GetMetadata<RateLimitMetadata>() is null)
            .Select(Describe)
            .ToList();
        Assert.True(missing.Count == 0, "Routes without a RoutePolicy or RateLimit:\n" + string.Join('\n', missing));
    }

    [Fact]
    public async Task Every_room_route_is_limited_per_ip_and_per_principal() {
        var endpoints = await EndpointsAsync();
        var roomPolicies = new[] { RoutePolicy.BoardAccess, RoutePolicy.BoardManage, RoutePolicy.SessionManage };

        var weak = endpoints
            .Where(x => x.Metadata.GetMetadata<RoutePolicyMetadata>() is { } policy && roomPolicies.Contains(policy.Policy))
            .Where(x => x.Metadata.GetMetadata<RateLimitMetadata>() is not { } limit
                || !limit.Limit.Has(RateScope.Ip)
                || !limit.Limit.Has(RateScope.Principal))
            .Select(Describe)
            .ToList();
        Assert.True(weak.Count == 0, "Room routes without per-IP and per-principal limits:\n" + string.Join('\n', weak));
    }

    // UX18: a signed-out visitor reaches only the shell (which redirects them from every page but the welcome, sign-in
    // and invite pages), sign-in itself, the invite preview, health and blobs (avatars; board images ask the policy).
    [Fact]
    public async Task Only_the_shell_sign_in_invite_preview_health_and_blob_routes_are_public() {
        var endpoints = await EndpointsAsync();
        string[] expected = [
            Health.Path,
            Shell.BootPath,
            "/invite/{token}",
            "/blob/{id:guid}",
            "/api/auth/signup",
            "/api/auth/login",
            "/api/auth/magic/request",
            "/api/auth/magic/preview",
            "/api/auth/magic/consume",
            "/api/auth/reset/request",
            "/api/auth/reset/consume",
            "/auth/oidc/{provider}/start",
            "/auth/oidc/{provider}/callback",
            "/api/invites/preview",
            .. ShellRoute.All.Select(x => x.Template),
        ];

        var open = endpoints
            .Where(x => x.Metadata.GetMetadata<RoutePolicyMetadata>()?.Policy == RoutePolicy.Public)
            .Select(x => x.RoutePattern.RawText!)
            .Order(StringComparer.Ordinal);

        Assert.Equal(expected.Order(StringComparer.Ordinal), open);
    }

    [Fact]
    public async Task Hosting_maps_health_boot_and_every_shell_route() {
        var patterns = (await EndpointsAsync()).Select(x => x.RoutePattern.RawText).ToHashSet(StringComparer.Ordinal);

        Assert.Contains(Health.Path, patterns);
        Assert.Contains(Shell.BootPath, patterns);
        foreach (var route in ShellRoute.All) {
            Assert.Contains(route.Template, patterns);
        }
    }

    [Fact]
    public void Shell_routes_mirror_the_frozen_front_end_route_table() {
        var source = File.ReadAllText(Path.Combine(RepoRoot.Path, "web/src/routes.js"));
        var paths = RoutePath().Matches(source).Select(x => x.Groups[1].Value);

        Assert.Equal(paths, ShellRoute.All.Select(x => x.Pattern));
    }

    private async Task<List<RouteEndpoint>> EndpointsAsync() {
        var database = await postgres.CreateDatabaseAsync();
        await using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => {
            builder.UseSetting("PUBLIC_URL", TestApp.PublicUrl);
            builder.UseSetting("DATABASE_URL", database.AppConnectionString);
            builder.UseSetting("DATABASE_OWNER_URL", database.OwnerConnectionString);
            builder.UseSetting("WEB_ROOT", Path.Combine(Path.GetTempPath(), "rs-no-web-build"));
        });
        using var client = factory.CreateClient();
        return [.. factory.Services.GetRequiredService<EndpointDataSource>().Endpoints.OfType<RouteEndpoint>()];
    }

    private static string Describe(RouteEndpoint endpoint) => $"{endpoint.RoutePattern.RawText} ({endpoint.DisplayName})";

    [GeneratedRegex("""path: '([^']+)'""")]
    private static partial Regex RoutePath();
}
