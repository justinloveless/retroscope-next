using Retroscope.Core.Room;

namespace Retroscope.Server.Hosting;

/// <summary>Who sent an HTTP request, from the session or guest cookie.</summary>
/// <param name="EmailVerified">Users only; guests are never verified.</param>
/// <param name="AuthSessionId">The sign-in session behind a user cookie; null for guests.</param>
public sealed record Caller(Principal Principal, bool EmailVerified, Guid? AuthSessionId = null) {
    public bool IsUser => Principal.Kind == PrincipalKind.User;
}

/// <summary>Resolves the request's cookies to a caller. Implemented by auth (U16); without one, every request is anonymous.</summary>
public interface ICallerResolver {
    ValueTask<Caller?> ResolveAsync(HttpContext context);
}

public static class CallerExtensions {
    /// <summary>The caller, resolved at most once per request.</summary>
    public static async ValueTask<Caller?> GetCallerAsync(this HttpContext context) {
        if (context.Features.Get<CallerFeature>() is { } cached) {
            return cached.Caller;
        }
        var resolver = context.RequestServices.GetService<ICallerResolver>();
        var caller = resolver is null ? null : await resolver.ResolveAsync(context);
        context.Features.Set(new CallerFeature(caller));
        return caller;
    }

    private sealed record CallerFeature(Caller? Caller);
}
