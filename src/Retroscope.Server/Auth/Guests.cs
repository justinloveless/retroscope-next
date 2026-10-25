using System.Diagnostics.CodeAnalysis;
using System.Text.RegularExpressions;
using Retroscope.Core.Ids;
using Retroscope.Core.Room;
using Retroscope.Core.Security;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Endpoints;

namespace Retroscope.Server.Auth;

/// <summary>
/// Guest principals (§e): an HttpOnly <c>__Host-rs_guest</c> cookie whose SHA-256 is in <c>guest_tokens</c>, valid
/// 180 days and slid at most daily. Signing in never merges a guest, and the guest cookie is kept, so the browser's
/// old seats stay the same guest when it signs out again.
/// </summary>
public sealed class Guests {
    public const string CookieName = "__Host-rs_guest";
    public const int CacheCapacity = 4096;

    public static readonly TimeSpan Lifetime = TimeSpan.FromDays(180);
    public static readonly TimeSpan SlideInterval = TimeSpan.FromDays(1);
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly GuestsRepo _repo;
    private readonly TimeProvider _time;
    private readonly TokenCache<GuestRow> _cache;

    public Guests(GuestsRepo repo, TimeProvider time) {
        _repo = repo;
        _time = time;
        _cache = new TokenCache<GuestRow>(time, CacheCapacity, CacheTtl);
    }

    public async Task<Principal?> CurrentAsync(HttpContext context) {
        var token = context.Request.Cookies[CookieName];
        if (token is null || !OpaqueToken.IsWellFormed(token)) {
            return null;
        }
        var hash = TokenHash.Of(token);
        var now = _time.GetUtcNow();
        if (!_cache.TryGet(hash, out var guest)) {
            guest = await _repo.LoadAsync(hash, context.RequestAborted);
            if (guest is null) {
                return null;
            }
            if (guest.ExpiresAt > now && guest.ExpiresAt - now < Lifetime - SlideInterval) {
                guest = guest with { ExpiresAt = now + Lifetime };
                await _repo.SlideAsync(hash, guest.ExpiresAt, context.RequestAborted);
                context.Response.Cookies.Append(CookieName, token, Sessions.CookieOptions(Lifetime));
            }
            _cache.Set(hash, guest);
        }
        return guest.ExpiresAt > now ? ToPrincipal(guest) : null;
    }

    public async Task<Principal> IssueAsync(HttpContext context, string displayName, CancellationToken cancellationToken) {
        var token = OpaqueToken.New();
        var id = new PrincipalId(Guid.CreateVersion7());
        var now = _time.GetUtcNow();
        await _repo.CreateAsync(id, displayName, TokenHash.Of(token), now, now + Lifetime, cancellationToken);
        context.Response.Cookies.Append(CookieName, token, Sessions.CookieOptions(Lifetime));
        return new Principal(id, PrincipalKind.Guest, displayName, null, false);
    }

    public async Task<Principal> RenameAsync(PrincipalId guest, string displayName, CancellationToken cancellationToken) {
        await _repo.RenameAsync(guest, displayName, cancellationToken);
        _cache.RemoveWhere(x => x.Id == guest);
        return new Principal(guest, PrincipalKind.Guest, displayName, null, false);
    }

    private static Principal ToPrincipal(GuestRow guest) => new(guest.Id, PrincipalKind.Guest, guest.DisplayName, null, false);
}

/// <summary>Guest names: 1–40 characters of plain text after trimming, never email-shaped (S21).</summary>
public static partial class GuestNames {
    public const int MaxLength = 40;

    /// <param name="error">A field error code: <c>required</c>, <c>too_long</c>, <c>invalid</c> or <c>email_shaped</c>.</param>
    public static bool TryNormalize(string? input, [NotNullWhen(true)] out string? name, [NotNullWhen(false)] out string? error) {
        name = null;
        var trimmed = input?.Trim() ?? "";
        if (trimmed.Length == 0) {
            error = "required";
            return false;
        }
        if (trimmed.Length > MaxLength) {
            error = "too_long";
            return false;
        }
        if (!TextRules.IsPlainText(trimmed)) {
            error = "invalid";
            return false;
        }
        if (EmailShape().IsMatch(trimmed)) {
            error = "email_shaped";
            return false;
        }
        name = trimmed;
        error = null;
        return true;
    }

    // Anything with an "@" between two non-space characters reads as an address to the people seeing the name.
    [GeneratedRegex(@"[^\s@]@[^\s@]", RegexOptions.CultureInvariant)]
    private static partial Regex EmailShape();
}
