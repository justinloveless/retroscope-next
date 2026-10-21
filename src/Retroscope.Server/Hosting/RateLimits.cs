using System.Globalization;

namespace Retroscope.Server.Hosting;

public enum RateScope {
    /// <summary>Per client IP (IPv6 by /64).</summary>
    Ip,

    /// <summary>Per principal; skipped for anonymous callers, whose IP rule still applies.</summary>
    Principal,

    /// <summary>Per caller-supplied subject (an email address, a board); skipped when none is given.</summary>
    Subject,

    /// <summary>One bucket for the whole process.</summary>
    Global,
}

/// <param name="Limit">Bucket capacity: this many requests in a burst, refilled evenly over the window.</param>
public sealed record RateRule(RateScope Scope, int Limit, TimeSpan Window) {
    public static RateRule PerIp(int limit, TimeSpan window) => new(RateScope.Ip, limit, window);

    public static RateRule PerPrincipal(int limit, TimeSpan window) => new(RateScope.Principal, limit, window);

    public static RateRule PerSubject(int limit, TimeSpan window) => new(RateScope.Subject, limit, window);

    public static RateRule Global(int limit, TimeSpan window) => new(RateScope.Global, limit, window);
}

/// <summary>
/// A named set of rules. Every endpoint declares one (§d); <see cref="Unlimited"/> is an explicit choice, not a
/// default. Units define their own next to their endpoints, e.g. <c>RateLimit.Define("auth.login", RateRule.PerIp(10, TimeSpan.FromMinutes(1)))</c>.
/// </summary>
public sealed class RateLimit {
    public static readonly RateLimit Unlimited = new("unlimited", []);

    private RateLimit(string name, IReadOnlyList<RateRule> rules) {
        Name = name;
        Rules = rules;
    }

    public string Name { get; }

    public IReadOnlyList<RateRule> Rules { get; }

    public bool Has(RateScope scope) => Rules.Any(x => x.Scope == scope);

    public static RateLimit Define(string name, params RateRule[] rules) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        if (rules.Length == 0) {
            throw new ArgumentException("A rate limit needs at least one rule; use RateLimit.Unlimited to opt out.", nameof(rules));
        }
        foreach (var rule in rules) {
            ArgumentOutOfRangeException.ThrowIfLessThan(rule.Limit, 1, nameof(rules));
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(rule.Window, TimeSpan.Zero, nameof(rules));
        }
        return new RateLimit(name, rules);
    }

    public override string ToString() => Name;
}

public sealed record RateLimitMetadata(RateLimit Limit);

public static class RateLimitExtensions {
    public static TBuilder RequireRateLimit<TBuilder>(this TBuilder builder, RateLimit limit)
        where TBuilder : IEndpointConventionBuilder =>
        builder.WithMetadata(new RateLimitMetadata(limit));
}

/// <summary>Limits owned by hosting. Other units define theirs beside their endpoints.</summary>
public static class RateLimits {
    public static readonly RateLimit Shell = RateLimit.Define("shell", RateRule.PerIp(300, TimeSpan.FromMinutes(1)));

    public static readonly RateLimit Boot = RateLimit.Define("boot", RateRule.PerIp(300, TimeSpan.FromMinutes(1)));

    /// <summary>Every room-code lookup, whichever route makes it (S9).</summary>
    public static readonly RateLimit RoomLookup = RateLimit.Define("room-code", RateRule.PerIp(30, TimeSpan.FromMinutes(1)));

    /// <summary>
    /// Applies the endpoint's declared limit before the handler runs. An endpoint without one is refused, so a
    /// forgotten declaration fails closed rather than unlimited.
    /// </summary>
    public static void Use(WebApplication app) {
        var limiter = app.Services.GetRequiredService<RateLimiter>();
        app.Use(async (context, next) => {
            if (context.GetEndpoint() is not RouteEndpoint endpoint) {
                await next(context);
                return;
            }
            var limit = endpoint.Metadata.GetMetadata<RateLimitMetadata>()?.Limit;
            if (limit is null) {
                await Problem.WriteAsync(context, StatusCodes.Status403Forbidden, "forbidden");
                return;
            }
            if (limit.Rules.Count > 0) {
                var caller = limit.Has(RateScope.Principal) ? await context.GetCallerAsync() : null;
                var key = new RateKeys(ClientIp.LimitKey(context), caller?.Principal.Id.ToString());
                if (!limiter.TryAcquire(limit, key, out var retryAfter)) {
                    await WriteLimitedAsync(context, retryAfter);
                    return;
                }
            }
            await next(context);
        });
    }

    public static IResult LimitedResult(TimeSpan retryAfter) => new LimitedHttpResult(retryAfter);

    public static Task WriteLimitedAsync(HttpContext context, TimeSpan retryAfter) {
        context.Response.Headers.RetryAfter = RetryAfterSeconds(retryAfter);
        return Problem.WriteAsync(context, StatusCodes.Status429TooManyRequests, "rate_limited");
    }

    private static string RetryAfterSeconds(TimeSpan retryAfter) =>
        Math.Max(1, (long)Math.Ceiling(retryAfter.TotalSeconds)).ToString(CultureInfo.InvariantCulture);

    private sealed class LimitedHttpResult(TimeSpan retryAfter) : IResult {
        public Task ExecuteAsync(HttpContext httpContext) => WriteLimitedAsync(httpContext, retryAfter);
    }
}

/// <param name="Ip">From <see cref="ClientIp.LimitKey(HttpContext)"/>.</param>
/// <param name="Principal">Null for anonymous callers.</param>
/// <param name="Subject">What a <see cref="RateScope.Subject"/> rule counts per.</param>
public readonly record struct RateKeys(string? Ip, string? Principal, string? Subject = null);
