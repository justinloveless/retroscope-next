using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using Retroscope.Core.Security;
using Retroscope.Server.Hosting;

namespace Retroscope.Server.Auth;

/// <summary>Outcome of work run behind the <see cref="HashGate"/>; not admitted means answer 503 <c>busy</c>.</summary>
public readonly record struct Gated<T>(bool Admitted, T Value);

/// <summary>
/// Every bcrypt hash and verify runs here (§e, F14, S31): <see cref="DefaultWidth"/> at a time (one core), each caller
/// waits at most <see cref="DefaultWait"/>, and at most <see cref="DefaultQueueCap"/> wait at once. Past either limit
/// the endpoint answers 503 <c>busy</c> with <c>Retry-After: 2</c> instead of queueing CPU work without bound.
/// </summary>
public sealed class HashGate : IDisposable {
    public const int DefaultWidth = 1;
    public const int DefaultQueueCap = 50;
    public const int RetryAfterSeconds = 2;

    public static readonly TimeSpan DefaultWait = TimeSpan.FromSeconds(2);

    private readonly SemaphoreSlim _slots;
    private readonly TimeSpan _wait;
    private readonly int _queueCap;
    private int _waiting;

    public HashGate(int width = DefaultWidth, TimeSpan? wait = null, int queueCap = DefaultQueueCap) {
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(queueCap);
        _slots = new SemaphoreSlim(width, width);
        Width = width;
        _wait = wait ?? DefaultWait;
        _queueCap = queueCap;
    }

    public int Width { get; }

    public int Waiting => Volatile.Read(ref _waiting);

    public async Task<Gated<T>> RunAsync<T>(Func<T> work, CancellationToken cancellationToken) {
        if (Interlocked.Increment(ref _waiting) > _queueCap) {
            Interlocked.Decrement(ref _waiting);
            return default;
        }
        bool entered;
        try {
            entered = await _slots.WaitAsync(_wait, cancellationToken);
        } finally {
            Interlocked.Decrement(ref _waiting);
        }
        if (!entered) {
            return default;
        }
        try {
            return new Gated<T>(true, work());
        } finally {
            _slots.Release();
        }
    }

    public static IResult BusyResult() => new BusyHttpResult();

    public void Dispose() => _slots.Dispose();

    private sealed class BusyHttpResult : IResult {
        public Task ExecuteAsync(HttpContext httpContext) {
            httpContext.Response.Headers.RetryAfter = RetryAfterSeconds.ToString(CultureInfo.InvariantCulture);
            return Problem.WriteAsync(httpContext, StatusCodes.Status503ServiceUnavailable, "busy");
        }
    }
}

/// <summary>
/// The password rule for signup and password change (§d, S30): 10–72 UTF-8 bytes and not a common password. Login
/// applies none of it, so imported 6-character passwords still sign in.
/// </summary>
public static class PasswordRules {
    public const int MinBytes = 10;
    public const int MaxBytes = Bcrypt.MaxPasswordBytes;

    // Every entry of SecLists Passwords/Common-Credentials/10k-most-common.txt (MIT; commit 913b327, sha256
    // 68782d6a…c50fdcce) that the length rule admits; the other 9,950 are shorter than MinBytes.
    private static readonly FrozenSet<string> Common = FrozenSet.Create(StringComparer.Ordinal, [
        "1234567890", "fuck_inside", "deepthroat", "qwertyuiop", "basketball", "masterbate", "penetration",
        "masterbating", "unbelievable", "intercourse", "interacial", "ejaculation", "businessbabe", "experience",
        "contortionist", "cheerleaers", "housewifes", "gangbanged", "experienced", "transexual", "lockerroom",
        "absolutely", "masterbaiting", "masturbation", "pornographic", "knickerless", "enterprise", "scandinavian",
        "techniques", "manchester", "penetrating", "films+pic+galeries", "uncencored", "insertions", "fuckinside",
        "quant4307s", "christopher", "california", "cocksucker", "washington", "postov1000", "charlie123",
        "primetime21", "dragonball", "highlander", "playstation", "undertaker", "university", "1q2w3e4r5t",
        "motherfucker", "peternorth",
    ]);

    /// <returns>Null when acceptable, else a field error code: <c>required</c>, <c>too_short</c>, <c>too_long</c> or <c>too_common</c>.</returns>
    public static string? Check(string? password) {
        if (string.IsNullOrEmpty(password)) {
            return "required";
        }
        var bytes = Encoding.UTF8.GetByteCount(password);
        if (bytes < MinBytes) {
            return "too_short";
        }
        if (bytes > MaxBytes) {
            return "too_long";
        }
        return Common.Contains(password.ToLowerInvariant()) ? "too_common" : null;
    }
}

/// <summary>
/// Login backoff (§e, S13), in memory. Failures are counted per (account, IP prefix): from the
/// <see cref="FreeFailures"/>th on, the pair waits 1 minute, doubling up to <see cref="MaxBackoff"/>. Separately each
/// account may fail <see cref="AccountCap"/> times an hour from all addresses together; past that, password sign-in
/// for the account pauses and the owner is mailed a sign-in link instead of being locked out. Accounts are keyed by
/// the normalized email, so an unknown email behaves exactly like a known one.
/// </summary>
public sealed class LoginThrottle(TimeProvider time) {
    public const int FreeFailures = 5;
    public const int AccountCap = 50;
    public const int Capacity = 100_000;

    public static readonly TimeSpan FirstBackoff = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromMinutes(15);
    public static readonly TimeSpan AccountWindow = TimeSpan.FromHours(1);

    /// <summary>A pair with no failure for this long starts counting from zero again.</summary>
    public static readonly TimeSpan ForgetAfter = TimeSpan.FromMinutes(30);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, LinkedListNode<PairState>> _pairs = new(StringComparer.Ordinal);
    private readonly LinkedList<PairState> _pairRecency = new();
    private readonly Dictionary<string, LinkedListNode<AccountState>> _accounts = new(StringComparer.Ordinal);
    private readonly LinkedList<AccountState> _accountRecency = new();

    /// <returns>How long the caller must wait before this account may try a password from this address; null when it may now.</returns>
    public TimeSpan? RetryAfter(string account, string ipPrefix) {
        var now = time.GetUtcNow();
        lock (_lock) {
            TimeSpan? wait = null;
            if (Find(_pairs, PairKey(account, ipPrefix)) is { } pair && pair.BlockedUntil > now) {
                wait = pair.BlockedUntil - now;
            }
            if (Find(_accounts, account) is { } state) {
                Refill(state, now);
                if (state.Tokens < 1) {
                    var refill = TimeSpan.FromTicks((long)((1 - state.Tokens) * AccountWindow.Ticks / AccountCap));
                    wait = wait is { } w && w > refill ? w : refill;
                }
            }
            return wait;
        }
    }

    /// <returns>True when this failure exhausted the account's hourly cap, i.e. the owner should be mailed an unlock link now.</returns>
    public bool RecordFailure(string account, string ipPrefix) {
        var now = time.GetUtcNow();
        lock (_lock) {
            var pair = Touch(_pairs, _pairRecency, PairKey(account, ipPrefix), key => new PairState(key));
            if (now - pair.LastFailure > ForgetAfter) {
                pair.Failures = 0;
            }
            pair.Failures++;
            pair.LastFailure = now;
            if (pair.Failures >= FreeFailures) {
                var doublings = Math.Min(pair.Failures - FreeFailures, 4);
                var backoff = FirstBackoff * (1 << doublings);
                pair.BlockedUntil = now + (backoff < MaxBackoff ? backoff : MaxBackoff);
            }

            var state = Touch(_accounts, _accountRecency, account, key => new AccountState(key, AccountCap, now));
            Refill(state, now);
            state.Tokens -= 1;
            if (state.Tokens < 1 && !state.Capped) {
                state.Capped = true;
                return true;
            }
            return false;
        }
    }

    public void RecordSuccess(string account, string ipPrefix) {
        lock (_lock) {
            if (_pairs.Remove(PairKey(account, ipPrefix), out var node)) {
                _pairRecency.Remove(node);
            }
        }
    }

    private static string PairKey(string account, string ipPrefix) => $"{ipPrefix}|{account}";

    private static T? Find<T>(Dictionary<string, LinkedListNode<T>> map, string key) where T : class =>
        map.TryGetValue(key, out var node) ? node.Value : null;

    private static T Touch<T>(Dictionary<string, LinkedListNode<T>> map, LinkedList<T> recency, string key, Func<string, T> create)
        where T : class, IKeyed {
        if (map.TryGetValue(key, out var node)) {
            recency.Remove(node);
            recency.AddFirst(node);
            return node.Value;
        }
        if (map.Count >= Capacity) {
            map.Remove(recency.Last!.Value.Key);
            recency.RemoveLast();
        }
        var value = create(key);
        map[key] = recency.AddFirst(value);
        return value;
    }

    private static void Refill(AccountState state, DateTimeOffset now) {
        var elapsed = now - state.UpdatedAt;
        state.UpdatedAt = now;
        if (elapsed > TimeSpan.Zero) {
            state.Tokens = Math.Min(AccountCap, state.Tokens + (elapsed.Ticks * (double)AccountCap / AccountWindow.Ticks));
        }
        if (state.Tokens >= 1) {
            state.Capped = false;
        }
    }

    private interface IKeyed {
        string Key { get; }
    }

    private sealed class PairState(string key) : IKeyed {
        public string Key { get; } = key;

        public int Failures { get; set; }

        public DateTimeOffset LastFailure { get; set; }

        public DateTimeOffset BlockedUntil { get; set; }
    }

    private sealed class AccountState(string key, double tokens, DateTimeOffset updatedAt) : IKeyed {
        public string Key { get; } = key;

        public double Tokens { get; set; } = tokens;

        public DateTimeOffset UpdatedAt { get; set; } = updatedAt;

        public bool Capped { get; set; }
    }
}
