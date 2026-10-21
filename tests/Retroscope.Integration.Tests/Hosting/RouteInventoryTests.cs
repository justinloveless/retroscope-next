using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Retroscope.Integration.Tests.Scaffold;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Hosting;

namespace Retroscope.Integration.Tests.Hosting;

// §d/§j: every route of the real server declares a RoutePolicy and a RateLimit, and every guest-reachable route is
// limited per IP and per principal (S7). Unsafe methods need no metadata: CSRF is a global middleware.
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
    public async Task Every_guest_reachable_route_is_limited_per_ip_and_per_principal() {
        var endpoints = await EndpointsAsync();
        var guestPolicies = new[] { RoutePolicy.Guest, RoutePolicy.BoardAccess, RoutePolicy.BoardManage, RoutePolicy.SessionManage };

        var weak = endpoints
            .Where(x => x.Metadata.GetMetadata<RoutePolicyMetadata>() is { } policy && guestPolicies.Contains(policy.Policy))
            .Where(x => x.Metadata.GetMetadata<RateLimitMetadata>() is not { } limit
                || !limit.Limit.Has(RateScope.Ip)
                || !limit.Limit.Has(RateScope.Principal))
            .Select(Describe)
            .ToList();
        Assert.True(weak.Count == 0, "Guest-reachable routes without per-IP and per-principal limits:\n" + string.Join('\n', weak));
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
