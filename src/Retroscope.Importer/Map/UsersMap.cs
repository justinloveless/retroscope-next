using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Npgsql;
using NpgsqlTypes;
using Retroscope.Core.Security;
using Retroscope.Importer.Source;

namespace Retroscope.Importer.Map;

/// <summary>
/// <c>auth.users</c> + <c>public.profiles</c> → <c>principals</c> and <c>users</c> (DESIGN §h.2). Kept accounts become
/// users, anonymous accounts become guests, merged duplicates go to the archive whole (their password sealed), and
/// accounts without an email are skipped (S29). Every imported account starts unverified (S1) unless it has a Google
/// identity for the same email. <c>profiles.role</c> is never trusted: only <c>IMPORT_ADMIN_EMAILS</c>
/// makes an admin. Avatars are attached by the storage stage.
/// </summary>
public sealed class UsersMap : IImportStage {
    public const int MaxFullName = 100;
    public const int MaxNickname = 40;

    public int Order => 100;

    public string Name => "users";

    public async Task RunAsync(ImportContext context, CancellationToken cancellationToken) {
        var accounts = await context.Source.ReadAsync("auth.users", cancellationToken);
        var profiles = (await context.Source.ReadAsync("public.profiles", cancellationToken)).ToDictionary(x => x.Uuid("id")!.Value);
        var verified = GoogleVerified(await context.Source.ReadAsync("auth.identities", cancellationToken), context.Principals);
        var accountsTally = context.Report.Table("auth.users");

        var principals = new List<object?[]>();
        var users = new List<object?[]>();
        foreach (var account in accounts) {
            var id = account.Uuid("id")!.Value;
            var profile = profiles.GetValueOrDefault(id);
            var createdAt = account.Timestamp("created_at") ?? context.ImportedAt;
            if (context.Principals.IsKeptUser(id)) {
                principals.Add([id, "user", null, createdAt]);
                users.Add(User(context, account, profile, verified.Contains(id)));
                accountsTally.Imported++;
            } else if (context.Principals.Guests.Contains(id)) {
                principals.Add([id, "guest", DisplayNames.Scrub(profile?.Text("full_name") ?? profile?.Text("nickname")), createdAt]);
                accountsTally.Imported++;
            } else if (context.Principals.User(id) is { } kept) {
                context.Archive.AddRow(account);
                accountsTally.Archived++;
                context.Report.Note("mergedAccounts", new JsonObject { ["id"] = id.ToString(), ["keptId"] = kept.ToString() });
            } else {
                accountsTally.Skip("phone_only");
                context.Report.Note("skippedPhoneOnly", id.ToString());
            }
        }

        var profilesTally = context.Report.Table("public.profiles");
        foreach (var (id, profile) in profiles) {
            if (context.Principals.IsKeptUser(id) || context.Principals.Guests.Contains(id)) {
                profilesTally.Imported++;
            } else if (context.Principals.User(id) is not null) {
                context.Archive.AddRow(profile);
                profilesTally.Archived++;
            } else {
                profilesTally.Skip("no_account");
            }
        }

        await TargetRows.CopyAsync(context.Target, "principals", [
            ("id", NpgsqlDbType.Uuid), ("kind", NpgsqlDbType.Text), ("display_name", NpgsqlDbType.Text), ("created_at", NpgsqlDbType.TimestampTz),
        ], principals, cancellationToken);
        await TargetRows.CopyAsync(context.Target, "users", [
            ("id", NpgsqlDbType.Uuid), ("email", NpgsqlDbType.Text), ("email_verified_at", NpgsqlDbType.TimestampTz),
            ("password_hash", NpgsqlDbType.Text), ("full_name", NpgsqlDbType.Text), ("nickname", NpgsqlDbType.Text),
            ("theme", NpgsqlDbType.Text), ("is_admin", NpgsqlDbType.Boolean), ("disabled_at", NpgsqlDbType.TimestampTz),
            ("mentions_seen_at", NpgsqlDbType.TimestampTz), ("created_at", NpgsqlDbType.TimestampTz), ("updated_at", NpgsqlDbType.TimestampTz),
        ], users, cancellationToken);
    }

    /// <summary>
    /// Kept users with an imported Google identity whose email equals the account email. Imported accounts are
    /// treated as unverified (S1), so only Google proves ownership of the address.
    /// </summary>
    public static IReadOnlySet<Guid> GoogleVerified(IEnumerable<SourceRow> identities, PrincipalDirectory principals) {
        var verified = new HashSet<Guid>();
        foreach (var identity in identities.Where(x => x.Text("provider") == ExternalIdentitiesMap.Google && !HasMalformedData(x))) {
            if (principals.User(identity.Uuid("user_id")) is { } user
                && PrincipalDirectory.NormalizeEmail(IdentityEmail(identity)) == principals.Emails[user]) {
                verified.Add(user);
            }
        }
        return verified;
    }

    private static object?[] User(ImportContext context, SourceRow account, SourceRow? profile, bool googleVerified) {
        var id = account.Uuid("id")!.Value;
        var email = context.Principals.Emails[id];
        if (email.Length is < 3 or > 254 || email.IndexOf('@', StringComparison.Ordinal) < 1) {
            throw new InvalidOperationException($"auth.users {id} has an email the target cannot store; fix it in the source.");
        }

        var hash = account.Text("encrypted_password");
        if (!Bcrypt.IsWellFormed(hash)) {
            if (!string.IsNullOrEmpty(hash)) {
                context.Report.Count("nulledPasswordHashes");
            }
            hash = null;
        }

        var fullName = TargetRows.Clip(profile?.Text("full_name")?.Trim() ?? "", MaxFullName);
        if (DisplayNames.LooksLikeEmail(fullName)) {
            context.Report.Note("emailsInFullName", id.ToString());
        }

        var isAdmin = context.AdminEmails.Contains(email);
        if (isAdmin) {
            context.Report.Note("admins", email);
        } else if (profile?.Text("role") == "admin") {
            context.Report.Note("ignoredAdminRoles", new JsonObject { ["id"] = id.ToString(), ["email"] = email });
        }

        var disabledAt = account.Timestamp("deleted_at")
            ?? (account.Timestamp("banned_until") > context.ImportedAt ? context.ImportedAt : null);
        if (disabledAt is not null) {
            context.Report.Note("disabledUsers", id.ToString());
        }

        var createdAt = account.Timestamp("created_at") ?? context.ImportedAt;
        return [
            id,
            email,
            googleVerified ? context.ImportedAt : null,
            hash,
            fullName,
            TargetRows.Clip(profile?.Text("nickname")?.Trim() ?? "", MaxNickname) is { Length: > 0 } nickname ? nickname : null,
            profile?.Text("theme_preference") is "light" or "dark" ? profile.Text("theme_preference") : "system",
            isAdmin,
            disabledAt,
            context.ImportedAt,
            createdAt,
            account.Timestamp("updated_at") ?? createdAt,
        ];
    }

    // auth.identities.email is generated from identity_data (and never read), so the address comes from the JSON itself.
    internal static string? IdentityEmail(SourceRow identity) =>
        TargetRows.ParseObject(identity.Text("identity_data"))?["email"] is JsonValue value && value.TryGetValue<string>(out var email)
            ? email
            : null;

    /// <summary>
    /// <c>identity_data</c> that is not a JSON object the parser accepts (jsonb can nest deeper than it allows). Such
    /// an identity proves nothing and is skipped.
    /// </summary>
    internal static bool HasMalformedData(SourceRow identity) =>
        identity.Text("identity_data") is { } json && TargetRows.ParseObject(json) is null;
}

/// <summary>
/// <c>auth.identities</c> → <c>external_identities</c>. Only Google identities carry over, keyed by the provider's
/// subject and following duplicate-email merges; email-provider rows say nothing the users row does not.
/// </summary>
public sealed class ExternalIdentitiesMap : IImportStage {
    public const string Google = "google";
    public const int MaxSubject = 200;

    public int Order => 110;

    public string Name => "external identities";

    public async Task RunAsync(ImportContext context, CancellationToken cancellationToken) {
        var tally = context.Report.Table("auth.identities");
        var rows = new List<object?[]>();
        foreach (var identity in await context.Source.ReadAsync("auth.identities", cancellationToken)) {
            var provider = identity.Text("provider");
            var subject = identity.Text("provider_id") ?? "";
            if (provider != Google) {
                tally.Skip(provider == "email" ? "email_provider" : "unsupported_provider");
            } else if (UsersMap.HasMalformedData(identity)) {
                tally.Skip("malformed_identity_data");
            } else if (context.Principals.User(identity.Uuid("user_id")) is not { } user) {
                tally.Skip("no_account");
            } else if (subject.Length is 0 or > MaxSubject) {
                tally.Skip("invalid_subject");
            } else {
                rows.Add([Google, subject, user, identity.Timestamp("created_at") ?? context.ImportedAt]);
                tally.Imported++;
            }
        }

        await TargetRows.CopyAsync(context.Target, "external_identities", [
            ("provider", NpgsqlDbType.Text), ("subject", NpgsqlDbType.Text), ("user_id", NpgsqlDbType.Uuid), ("linked_at", NpgsqlDbType.TimestampTz),
        ], rows, cancellationToken);
    }
}

/// <summary>Binary COPY into the target and the text limits the target's CHECKs count in characters (code points).</summary>
internal static class TargetRows {
    public static async Task CopyAsync(
        NpgsqlConnection target,
        string table,
        IReadOnlyList<(string Name, NpgsqlDbType Type)> columns,
        IEnumerable<object?[]> rows,
        CancellationToken cancellationToken) {
        var sql = $"copy {table} ({string.Join(", ", columns.Select(x => x.Name))}) from stdin (format binary)";
        await using var importer = await target.BeginBinaryImportAsync(sql, cancellationToken);
        foreach (var row in rows) {
            await importer.StartRowAsync(cancellationToken);
            for (var i = 0; i < columns.Count; i++) {
                if (row[i] is null) {
                    await importer.WriteNullAsync(cancellationToken);
                } else {
                    await importer.WriteAsync(row[i], columns[i].Type, cancellationToken);
                }
            }
        }
        await importer.CompleteAsync(cancellationToken);
    }

    public static async Task<HashSet<Guid>> IdsAsync(NpgsqlConnection target, string table, CancellationToken cancellationToken) {
        var ids = new HashSet<Guid>();
        await using var command = new NpgsqlCommand($"select id from {table}", target);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            ids.Add(reader.GetGuid(0));
        }
        return ids;
    }

    /// <summary>Postgres <c>char_length</c>: Unicode scalar values, not UTF-16 units.</summary>
    public static int Length(string text) => text.EnumerateRunes().Count();

    /// <summary>The first <paramref name="max"/> characters (never splitting a surrogate pair), trailing space trimmed.</summary>
    public static string Clip(string text, int max) {
        if (Length(text) <= max) {
            return text;
        }
        var builder = new StringBuilder();
        foreach (var rune in text.EnumerateRunes().Take(max)) {
            builder.Append(rune.ToString());
        }
        return builder.ToString().TrimEnd();
    }

    /// <summary>Plain one-line text: control characters become spaces, runs of white space collapse, ends trimmed.</summary>
    public static string Plain(string text) =>
        string.Join(' ', new string([.. text.Select(c => char.IsControl(c) ? ' ' : c)])
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public static JsonObject? ParseObject(string? json) {
        if (json is null) {
            return null;
        }
        try {
            return JsonNode.Parse(json) as JsonObject;
        } catch (JsonException) {
            return null;
        }
    }
}
