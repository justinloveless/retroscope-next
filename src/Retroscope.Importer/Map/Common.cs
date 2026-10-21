using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Security;
using Retroscope.Importer.Archive;
using Retroscope.Importer.Source;

namespace Retroscope.Importer.Map;

/// <summary>
/// One step of the main transaction (DESIGN §h.1 phase 6–8). Stages are discovered by reflection and run by ascending
/// <see cref="Order"/>, so mapping units add a class instead of editing a shared list. Conventions: identity 100–199,
/// teams 200–299, poker 300–399, retro 400–499, storage 500–599, archive 900, verification 950+.
/// </summary>
public interface IImportStage {
    int Order { get; }

    string Name { get; }

    Task RunAsync(ImportContext context, CancellationToken cancellationToken);
}

/// <summary>
/// A hard invariant (§h.5). Runs inside the main transaction before COMMIT (a non-empty result rolls everything back)
/// and on its own in the <c>verify</c> verb, where <see cref="VerifyContext.Report"/> is null.
/// </summary>
public interface IVerifyCheck {
    string Name { get; }

    Task<IReadOnlyList<string>> RunAsync(VerifyContext context, CancellationToken cancellationToken);
}

public sealed record VerifyContext(NpgsqlConnection Target, ImportReport? Report);

public static class ImportStages {
    public static IReadOnlyList<IImportStage> Discover() =>
        [.. Create<IImportStage>().OrderBy(x => x.Order).ThenBy(x => x.Name, StringComparer.Ordinal)];

    public static IReadOnlyList<IVerifyCheck> DiscoverChecks() =>
        [.. Create<IVerifyCheck>().OrderBy(x => x.Name, StringComparer.Ordinal)];

    private static IEnumerable<T> Create<T>() =>
        typeof(ImportStages).Assembly.GetTypes()
            .Where(x => x is { IsClass: true, IsAbstract: false } && typeof(T).IsAssignableFrom(x) && x.GetConstructor(Type.EmptyTypes) is not null)
            .Select(x => (T)Activator.CreateInstance(x)!);
}

/// <summary>Production keys the imported data must work with (encrypted Jira boxes, anonymous tags).</summary>
public sealed record ImportKeys(Keyring Keyring, IAnonTagger AnonTagger);

/// <summary>Everything a stage needs. The target connection is inside the main transaction.</summary>
public sealed class ImportContext {
    public required Guid RunId { get; init; }

    /// <summary>UTC; also the reference for "cutover + 7 days" invite caps and <c>email_verified_at</c>.</summary>
    public required DateTime ImportedAt { get; init; }

    public required SourceSnapshot Source { get; init; }

    public required NpgsqlConnection Target { get; init; }

    public required ArchiveWriter Archive { get; init; }

    public required ImportReport Report { get; init; }

    public required ImportKeys Keys { get; init; }

    public required PrincipalDirectory Principals { get; init; }

    public required IReadOnlySet<string> AdminEmails { get; init; }

    public required JiraHostPolicy JiraHosts { get; init; }

    /// <summary>The actor-reference rule (S3): the imported user behind <paramref name="id"/>, or null (counted).</summary>
    public Guid? UserRef(string table, string column, Guid? id) => Count(table, column, id, Principals.User(id));

    /// <summary>Like <see cref="UserRef"/> but guests (anonymous auth users) are valid targets too.</summary>
    public Guid? PrincipalRef(string table, string column, Guid? id) => Count(table, column, id, Principals.Principal(id));

    private Guid? Count(string table, string column, Guid? id, Guid? resolved) {
        if (id is not null && resolved is null) {
            Report.Count($"nulledActorRefs.{table}.{column}");
        }
        return resolved;
    }
}

public sealed record AuthAccount(Guid Id, string? Email, bool IsAnonymous, DateTime? LastSignInAt, DateTime? CreatedAt) {
    public static AuthAccount FromRow(SourceRow row) => new(
        row.Uuid("id")!.Value,
        row.Text("email"),
        row.Bool("is_anonymous") ?? false,
        row.Timestamp("last_sign_in_at"),
        row.Timestamp("created_at"));
}

/// <summary>
/// Which <c>auth.users</c> become principals (DESIGN §h.1 global rules, §h.2). Emails are trimmed and lower-cased.
/// Accounts sharing an email are merged into the one with the latest <c>last_sign_in_at</c> (then the earliest
/// <c>created_at</c>, then the smallest id, so the choice is deterministic); every loser id resolves to the kept id.
/// Anonymous accounts become guests. Other accounts without an email (phone-only signups) are skipped (S29).
/// </summary>
public sealed class PrincipalDirectory {
    private readonly Dictionary<Guid, Guid> _users;
    private readonly HashSet<Guid> _guests;

    private PrincipalDirectory(Dictionary<Guid, Guid> users, HashSet<Guid> guests, Dictionary<Guid, string> emails, List<Guid> phoneOnly) {
        _users = users;
        _guests = guests;
        Emails = emails;
        SkippedPhoneOnly = phoneOnly;
    }

    /// <summary>Kept user id → normalized email.</summary>
    public IReadOnlyDictionary<Guid, string> Emails { get; }

    /// <summary>Loser id → kept id, for merged duplicate emails only.</summary>
    public IReadOnlyDictionary<Guid, Guid> Merged => _users.Where(x => x.Key != x.Value).ToDictionary();

    public IReadOnlySet<Guid> Guests => _guests;

    public IReadOnlyList<Guid> SkippedPhoneOnly { get; }

    public static PrincipalDirectory Build(IEnumerable<AuthAccount> accounts) {
        var users = new Dictionary<Guid, Guid>();
        var guests = new HashSet<Guid>();
        var emails = new Dictionary<Guid, string>();
        var phoneOnly = new List<Guid>();
        var named = new List<(AuthAccount Account, string Email)>();
        foreach (var account in accounts) {
            var email = NormalizeEmail(account.Email);
            if (account.IsAnonymous) {
                guests.Add(account.Id);
            } else if (email is null) {
                phoneOnly.Add(account.Id);
            } else {
                named.Add((account, email));
            }
        }

        foreach (var group in named.GroupBy(x => x.Email, StringComparer.Ordinal)) {
            var kept = group
                .OrderByDescending(x => x.Account.LastSignInAt ?? DateTime.MinValue)
                .ThenBy(x => x.Account.CreatedAt ?? DateTime.MaxValue)
                .ThenBy(x => x.Account.Id.ToString(), StringComparer.Ordinal)
                .First().Account.Id;
            emails[kept] = group.Key;
            foreach (var (account, _) in group) {
                users[account.Id] = kept;
            }
        }

        phoneOnly.Sort();
        return new PrincipalDirectory(users, guests, emails, phoneOnly);
    }

    public static string? NormalizeEmail(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

    /// <summary>The kept user for a source user id (following merges), or null when it was not imported as a user.</summary>
    public Guid? User(Guid? id) => id is { } value && _users.TryGetValue(value, out var kept) ? kept : null;

    /// <summary>A user (following merges) or a guest account; null for anything else.</summary>
    public Guid? Principal(Guid? id) => User(id) ?? (id is { } value && _guests.Contains(value) ? value : null);

    public bool IsKeptUser(Guid id) => Emails.ContainsKey(id);
}

/// <summary>Synthetic principals (DESIGN §h.1 idempotency): UUIDv5 under fixed namespaces, stable across runs.</summary>
public static class SyntheticIds {
    public static readonly Guid GuestNamespace = new("6f1c2a8e-3b5d-4e7a-9c41-2d8b7e5f0a13");
    public static readonly Guid SlackNamespace = new("b2e94d17-8a60-4c3f-a5d2-91f7c03e6b48");

    /// <summary><c>v5(guest:&lt;session_id or localStorage id&gt;)</c>.</summary>
    public static Guid Guest(string key) => UuidV5.Create(GuestNamespace, "guest:" + key);

    /// <summary><c>v5(slack:&lt;key&gt;)</c>.</summary>
    public static Guid Slack(string key) => UuidV5.Create(SlackNamespace, "slack:" + key);
}

/// <summary>
/// The display-name scrub (S27) for every imported free-text name: anything containing an email address becomes
/// <c>Guest-xxxx</c> (four hex digits of the name's SHA-256, so re-runs agree). Names are trimmed and capped at 60
/// characters (<c>principals.display_name</c>); an empty name becomes <c>Guest</c>.
/// </summary>
public static partial class DisplayNames {
    public const int MaxLength = 60;

    public static string Scrub(string? name) {
        var trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0) {
            return "Guest";
        }
        if (LooksLikeEmail(trimmed)) {
            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(trimmed.ToLowerInvariant()));
            return "Guest-" + Convert.ToHexStringLower(hash)[..4];
        }
        return trimmed.Length <= MaxLength ? trimmed : trimmed[..MaxLength].TrimEnd();
    }

    public static bool LooksLikeEmail(string? text) => text is not null && Email().IsMatch(text);

    [GeneratedRegex(@"[^\s@<>()\[\]]+@[^\s@<>()\[\]]+\.[A-Za-z]{2,}", RegexOptions.CultureInvariant)]
    private static partial Regex Email();
}

/// <summary>Anonymity (S6): content extracted from an anonymous card or comment never records who made it.</summary>
public static class Anonymity {
    public static Guid? CreatedBy(bool anonymousSource, Guid? author) => anonymousSource ? null : author;
}
