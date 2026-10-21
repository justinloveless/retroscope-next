using System.Net;
using Retroscope.Core.Ids;

namespace Retroscope.Server.Hosting;

/// <summary>
/// Gates every outbound mail (S8): per recipient 3/h, per IP 20/h, per sending user 20/day, global 200/h. Over
/// budget the caller still answers 202 (no oracle); the mail is dropped and counted for <c>/api/admin/stats</c>.
/// </summary>
public sealed class EmailBudget(RateLimiter limiter) {
    public static readonly RateLimit Recipient = RateLimit.Define("email.recipient", RateRule.PerSubject(3, TimeSpan.FromHours(1)));

    public static readonly RateLimit SourceIp = RateLimit.Define("email.ip", RateRule.PerIp(20, TimeSpan.FromHours(1)));

    public static readonly RateLimit Sender = RateLimit.Define("email.sender", RateRule.PerPrincipal(20, TimeSpan.FromDays(1)));

    public static readonly RateLimit Total = RateLimit.Define("email.global", RateRule.Global(200, TimeSpan.FromHours(1)));

    // One limit with every rule, so a mail refused by one budget is not charged to the others.
    private static readonly RateLimit All = RateLimit.Define(
        "email",
        [.. Recipient.Rules, .. SourceIp.Rules, .. Sender.Rules, .. Total.Rules]);

    private long _dropped;

    public long Dropped => Interlocked.Read(ref _dropped);

    /// <param name="sender">The signed-in user who caused the mail (an invite); null for signup, reset and magic links.</param>
    /// <returns>False when the mail must be dropped.</returns>
    public bool TryReserve(string recipient, IPAddress? ip, PrincipalId? sender) {
        ArgumentException.ThrowIfNullOrWhiteSpace(recipient);
        var keys = new RateKeys(ClientIp.LimitKey(ip), sender?.ToString(), recipient.Trim().ToLowerInvariant());
        if (limiter.TryAcquire(All, keys, out _)) {
            return true;
        }
        Interlocked.Increment(ref _dropped);
        return false;
    }
}
