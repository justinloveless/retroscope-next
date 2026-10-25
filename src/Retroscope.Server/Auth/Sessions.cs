using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Serialization;
using Retroscope.Core.Ids;
using Retroscope.Core.Room;
using Retroscope.Core.Security;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Hosting;

namespace Retroscope.Server.Auth;

[JsonConverter(typeof(StrictEnumConverter<AuthMethod>))]
public enum AuthMethod {
    [JsonStringEnumMemberName("password")] Password,
    [JsonStringEnumMemberName("google")] Google,
    [JsonStringEnumMemberName("entra")] Entra,
    [JsonStringEnumMemberName("magic_link")] MagicLink,
}

public static class AuthMethods {
    public static string ToDb(AuthMethod method) => method switch {
        AuthMethod.Password => "password",
        AuthMethod.Google => "google",
        AuthMethod.Entra => "entra",
        AuthMethod.MagicLink => "magic_link",
        _ => throw new ArgumentOutOfRangeException(nameof(method)),
    };

    public static AuthMethod FromDb(string value) => value switch {
        "password" => AuthMethod.Password,
        "google" => AuthMethod.Google,
        "entra" => AuthMethod.Entra,
        "magic_link" => AuthMethod.MagicLink,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown auth method."),
    };
}

/// <summary>A live sign-in session. <see cref="Id"/> is the public handle (account page, socket revocation).</summary>
public sealed record SessionView(
    byte[] IdHash,
    AuthMethod Method,
    DateTimeOffset CreatedAt,
    DateTimeOffset LastSeenAt,
    DateTimeOffset IdleExpiresAt,
    DateTimeOffset AbsExpiresAt,
    Principal User,
    bool EmailVerified) {
    public Guid Id => Sessions.PublicId(IdHash);

    public bool IsLive(DateTimeOffset now) => now < IdleExpiresAt && now < AbsExpiresAt;

    public Caller ToCaller() => new(User, EmailVerified, Id);
}

/// <summary>
/// Sign-in sessions (§e): an opaque <c>__Host-rs_sid</c> cookie whose SHA-256 is the row key, sliding 30-day idle and
/// 90-day absolute expiry, <c>last_seen_at</c> written at most every 5 minutes. An LRU of 4,096 entries holds each
/// resolved session for 60 s, so most requests make no query; every revocation evicts and closes the session's sockets.
/// </summary>
public sealed class Sessions {
    public const string CookieName = "__Host-rs_sid";

    /// <summary>
    /// The cookie that identified a guest before UX18. Nothing reads it any more; signing in and out expires it, so a
    /// browser stops sending a credential that no longer means anything.
    /// </summary>
    public const string LegacyGuestCookieName = "__Host-rs_guest";
    public const int CacheCapacity = 4096;

    public static readonly TimeSpan IdleLifetime = TimeSpan.FromDays(30);
    public static readonly TimeSpan AbsoluteLifetime = TimeSpan.FromDays(90);
    public static readonly TimeSpan TouchInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(60);

    private readonly SessionsRepo _repo;
    private readonly TimeProvider _time;
    private readonly IEnumerable<IConnectionRevoker> _revokers;
    private readonly TokenCache<SessionView> _cache;

    public Sessions(SessionsRepo repo, TimeProvider time, IEnumerable<IConnectionRevoker> revokers) {
        _repo = repo;
        _time = time;
        _revokers = revokers;
        _cache = new TokenCache<SessionView>(time, CacheCapacity, CacheTtl);
    }

    public int CachedCount => _cache.Count;

    /// <summary>The public id of a session: the first 16 bytes of its token hash. It reveals nothing about the token.</summary>
    public static Guid PublicId(ReadOnlySpan<byte> idHash) => new(idHash[..16]);

    public async Task<SessionView?> ResolveAsync(string? token, CancellationToken cancellationToken) {
        if (token is null || !OpaqueToken.IsWellFormed(token)) {
            return null;
        }
        var hash = TokenHash.Of(token);
        var now = _time.GetUtcNow();
        if (_cache.TryGet(hash, out var cached)) {
            return cached.IsLive(now) ? cached : null;
        }

        var row = await _repo.LoadAsync(hash, cancellationToken);
        if (row is null || row.Disabled) {
            return null;
        }
        var session = new SessionView(
            row.IdHash, row.Method, row.CreatedAt, row.LastSeenAt, row.IdleExpiresAt, row.AbsExpiresAt, row.User, row.EmailVerified);
        if (!session.IsLive(now)) {
            return null;
        }
        if (now - session.LastSeenAt >= TouchInterval) {
            var idle = Min(now + IdleLifetime, session.AbsExpiresAt);
            await _repo.TouchAsync(hash, now, idle, cancellationToken);
            session = session with { LastSeenAt = now, IdleExpiresAt = idle };
        }
        _cache.Set(hash, session);
        return session;
    }

    public Task<SessionView?> CurrentAsync(HttpContext context) =>
        ResolveAsync(context.Request.Cookies[CookieName], context.RequestAborted);

    /// <summary>
    /// Starts a session and sets the cookie. A session the request already carries is revoked first, so every sign-in
    /// rotates the token (§e) and a planted cookie cannot survive it.
    /// </summary>
    public async Task<SessionView> SignInAsync(
        HttpContext context,
        Principal user,
        bool emailVerified,
        AuthMethod method,
        CancellationToken cancellationToken) {
        if (await CurrentAsync(context) is { } previous) {
            await RevokeAsync(previous.User.Id, [previous.IdHash], cancellationToken);
        }

        var token = OpaqueToken.New();
        var hash = TokenHash.Of(token);
        var now = _time.GetUtcNow();
        var session = new SessionView(hash, method, now, now, now + IdleLifetime, now + AbsoluteLifetime, user, emailVerified);
        await _repo.InsertAsync(
            SessionsRepo.Insert(hash, user.Id, method, now, session.IdleExpiresAt, session.AbsExpiresAt, context.Request.Headers.UserAgent.ToString()),
            cancellationToken);
        context.Response.Cookies.Append(CookieName, token, CookieOptions(AbsoluteLifetime));
        ExpireLegacyGuestCookie(context);
        return session;
    }

    /// <summary>Deletes the given sessions of <paramref name="user"/>, evicts them and closes their sockets.</summary>
    /// <returns>How many sessions were deleted.</returns>
    public async Task<int> RevokeAsync(PrincipalId user, IReadOnlyCollection<byte[]> idHashes, CancellationToken cancellationToken) {
        var deleted = await _repo.DeleteAsync(user, idHashes, cancellationToken);
        Revoked(deleted);
        return deleted.Count;
    }

    /// <summary>Deletes every session of the user except <paramref name="keep"/>.</summary>
    public async Task<int> RevokeAllAsync(PrincipalId user, byte[]? keep, CancellationToken cancellationToken) {
        var deleted = await _repo.DeleteAllAsync(user, keep, cancellationToken);
        Revoked(deleted);
        return deleted.Count;
    }

    /// <summary>For sessions deleted inside another transaction (a claim): evicts them and closes their sockets.</summary>
    public void Revoked(IEnumerable<byte[]> idHashes) {
        foreach (var hash in idHashes) {
            _cache.Remove(hash);
            foreach (var revoker in _revokers) {
                revoker.RevokeAuthSession(PublicId(hash));
            }
        }
    }

    /// <summary>Drops cached sessions of the user after a profile change, so the next request sees the new name.</summary>
    public void EvictUser(PrincipalId user) => _cache.RemoveWhere(x => x.User.Id == user);

    public static void ClearCookie(HttpResponse response) {
        response.Cookies.Append(CookieName, "", CookieOptions(TimeSpan.Zero));
        ExpireLegacyGuestCookie(response.HttpContext);
    }

    private static void ExpireLegacyGuestCookie(HttpContext context) {
        if (context.Request.Cookies.ContainsKey(LegacyGuestCookieName)) {
            context.Response.Cookies.Append(LegacyGuestCookieName, "", CookieOptions(TimeSpan.Zero));
        }
    }

    internal static CookieOptions CookieOptions(TimeSpan maxAge) => new() {
        HttpOnly = true,
        Secure = true,
        SameSite = SameSiteMode.Lax,
        Path = "/",
        MaxAge = maxAge,
        IsEssential = true,
    };

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a < b ? a : b;
}

/// <summary>Resolves the request's caller from a live session cookie: every caller is a signed-in user (UX18).</summary>
public sealed class AuthCallerResolver(Sessions sessions) : ICallerResolver {
    public async ValueTask<Caller?> ResolveAsync(HttpContext context) {
        return await sessions.CurrentAsync(context) is { } session ? session.ToCaller() : null;
    }
}

/// <summary>A bounded LRU from a token hash to what it resolved to, each entry valid for a fixed time.</summary>
internal sealed class TokenCache<T>(TimeProvider time, int capacity, TimeSpan ttl) where T : class {
    private readonly Lock _lock = new();
    private readonly Dictionary<string, LinkedListNode<Entry>> _entries = new(StringComparer.Ordinal);
    private readonly LinkedList<Entry> _recency = new();

    public int Count {
        get {
            lock (_lock) {
                return _entries.Count;
            }
        }
    }

    public bool TryGet(byte[] hash, [NotNullWhen(true)] out T? value) {
        var key = Convert.ToBase64String(hash);
        var now = time.GetUtcNow();
        lock (_lock) {
            if (_entries.TryGetValue(key, out var node)) {
                if (now - node.Value.StoredAt < ttl) {
                    _recency.Remove(node);
                    _recency.AddFirst(node);
                    value = node.Value.Value;
                    return true;
                }
                _recency.Remove(node);
                _entries.Remove(key);
            }
        }
        value = null;
        return false;
    }

    public void Set(byte[] hash, T value) {
        var key = Convert.ToBase64String(hash);
        var entry = new Entry(key, value, time.GetUtcNow());
        lock (_lock) {
            if (_entries.TryGetValue(key, out var existing)) {
                _recency.Remove(existing);
            } else if (_entries.Count >= capacity) {
                _entries.Remove(_recency.Last!.Value.Key);
                _recency.RemoveLast();
            }
            _entries[key] = _recency.AddFirst(entry);
        }
    }

    public void Remove(byte[] hash) {
        var key = Convert.ToBase64String(hash);
        lock (_lock) {
            if (_entries.Remove(key, out var node)) {
                _recency.Remove(node);
            }
        }
    }

    public void RemoveWhere(Func<T, bool> predicate) {
        lock (_lock) {
            var node = _recency.First;
            while (node is not null) {
                var next = node.Next;
                if (predicate(node.Value.Value)) {
                    _entries.Remove(node.Value.Key);
                    _recency.Remove(node);
                }
                node = next;
            }
        }
    }

    private sealed record Entry(string Key, T Value, DateTimeOffset StoredAt);
}
