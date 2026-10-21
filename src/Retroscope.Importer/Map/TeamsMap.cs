using System.Buffers;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using NpgsqlTypes;
using Retroscope.Core.Poker;
using Retroscope.Core.Security;
using Retroscope.Importer.Source;

namespace Retroscope.Importer.Map;

/// <summary>
/// <c>teams</c> → <c>teams</c> and <c>team_jira_config</c> (DESIGN §h.2). Imported Jira tokens are treated as exposed
/// (S10), so an imported config is sealed under the production keyring exactly as the server seals one
/// (<c>{"email","token"}</c>, AAD <c>"jira:v1:" ‖ team id</c>) and marked <c>rotate_required</c>. Field ids are left
/// for the first test to discover. Slack, advisor, org and tenant columns reach the archive through the archive stage.
/// </summary>
public sealed partial class TeamsMap : IImportStage {
    public const int MaxName = 100;
    public const int MaxDescription = 500;
    public const int MaxPointDescription = 100;
    public const string UntitledTeam = "Untitled team";

    public int Order => 200;

    public string Name => "teams";

    public async Task RunAsync(ImportContext context, CancellationToken cancellationToken) {
        var teams = new List<object?[]>();
        var jira = new List<object?[]>();
        foreach (var team in await context.Source.ReadAsync("public.teams", cancellationToken)) {
            var id = team.Uuid("id")!.Value;
            var createdAt = team.Timestamp("created_at") ?? context.ImportedAt;
            teams.Add([
                id,
                TeamName(context, id, team.Text("name")),
                TargetRows.Clip(team.Text("description")?.Trim() ?? "", MaxDescription) is { Length: > 0 } description ? description : null,
                context.UserRef("public.teams", "creator_id", team.Uuid("creator_id")),
                PointDescriptions(team.Text("poker_point_value_descriptions")).ToJsonString(),
                createdAt,
                team.Timestamp("updated_at") ?? createdAt,
            ]);
            if (JiraConfig(context, team) is { } config) {
                jira.Add(config);
            }
            context.Report.Table("public.teams").Imported++;
        }

        await TargetRows.CopyAsync(context.Target, "teams", [
            ("id", NpgsqlDbType.Uuid), ("name", NpgsqlDbType.Text), ("description", NpgsqlDbType.Text), ("created_by", NpgsqlDbType.Uuid),
            ("point_descriptions", NpgsqlDbType.Jsonb), ("created_at", NpgsqlDbType.TimestampTz), ("updated_at", NpgsqlDbType.TimestampTz),
        ], teams, cancellationToken);
        await TargetRows.CopyAsync(context.Target, "team_jira_config", [
            ("team_id", NpgsqlDbType.Uuid), ("site_host", NpgsqlDbType.Text), ("credentials_box", NpgsqlDbType.Bytea), ("key_id", NpgsqlDbType.Text),
            ("token_last4", NpgsqlDbType.Text), ("email_masked", NpgsqlDbType.Text), ("project_key", NpgsqlDbType.Text), ("board_id", NpgsqlDbType.Bigint),
            ("ticket_prefix", NpgsqlDbType.Text), ("status", NpgsqlDbType.Text), ("updated_at", NpgsqlDbType.TimestampTz),
        ], jira, cancellationToken);
    }

    /// <summary>Byte-for-byte the server's <c>Credentials.AssociatedData</c> (U19 decision 1); the importer cannot reference the server.</summary>
    public static byte[] JiraAssociatedData(Guid team) {
        var aad = new byte[8 + 16];
        "jira:v1:"u8.CopyTo(aad);
        team.TryWriteBytes(aad.AsSpan(8), bigEndian: true, out _);
        return aad;
    }

    /// <summary>Deck keys only, values as plain text of at most 100 characters; anything else is dropped.</summary>
    public static JsonObject PointDescriptions(string? json) {
        var result = new JsonObject();
        if (TargetRows.ParseObject(json) is not { } source) {
            return result;
        }
        foreach (var points in Deck.Values) {
            var key = points.ToString(CultureInfo.InvariantCulture);
            if (source[key] is JsonValue value && value.TryGetValue<string>(out var text)
                && TargetRows.Clip(TargetRows.Plain(text), MaxPointDescription) is { Length: > 0 } plain) {
                result[key] = plain;
            }
        }
        return result;
    }

    private static string TeamName(ImportContext context, Guid id, string? source) {
        var name = TargetRows.Plain(source ?? "");
        if (name.Length == 0) {
            context.Report.Note("renamedTeams", id.ToString());
            return UntitledTeam;
        }
        var clipped = TargetRows.Clip(name, MaxName);
        if (clipped != name) {
            context.Report.Note("truncatedTeamNames", id.ToString());
        }
        return clipped;
    }

    private static object?[]? JiraConfig(ImportContext context, SourceRow team) {
        var id = team.Uuid("id")!.Value;
        var domain = team.Text("jira_domain");
        var email = team.Text("jira_email")?.Trim();
        var token = team.Text("jira_api_key")?.Trim();
        if (string.IsNullOrWhiteSpace(domain) && string.IsNullOrEmpty(token)) {
            return null;
        }

        string? reason = null;
        if (!context.JiraHosts.TryNormalize(domain, out var host)) {
            reason = "host";
        } else if (string.IsNullOrEmpty(email)) {
            reason = "email";
        } else if (token is not { Length: >= 4 }) {
            reason = "token";
        }
        if (reason is not null) {
            context.Report.Note("jiraConfigsRejected", new JsonObject { ["team"] = id.ToString(), ["reason"] = reason });
            return null;
        }

        var box = SealCredentials(context.Keys.Keyring, id, email!, token!);
        Keyring.TryReadKeyId(box, out var keyId);
        var (projectKey, boardId) = BoardOrProject(team.Text("jira_board_id"));
        var prefix = team.Text("jira_ticket_prefix")?.Trim().TrimEnd('-').ToUpperInvariant();
        context.Report.Note("jiraRotateRequired", new JsonObject { ["team"] = id.ToString(), ["siteHost"] = host });
        return [
            id,
            host,
            box,
            keyId,
            token![^4..],
            MaskEmail(email!),
            projectKey,
            boardId,
            prefix is not null && JiraKey().IsMatch(prefix) ? prefix : null,
            "rotate_required",
            context.ImportedAt,
        ];
    }

    private static byte[] SealCredentials(Keyring keyring, Guid team, string email, string token) {
        var buffer = new ArrayBufferWriter<byte>(256);
        try {
            using (var writer = new Utf8JsonWriter(buffer)) {
                writer.WriteStartObject();
                writer.WriteString("email"u8, email);
                writer.WriteString("token"u8, token);
                writer.WriteEndObject();
            }
            return keyring.Seal(KeyPurpose.JiraCredentials, buffer.WrittenSpan, JiraAssociatedData(team));
        } finally {
            // Clear zeroes what was written, so the token does not outlive the call.
            buffer.Clear();
        }
    }

    // The original stored either a numeric board id or a project key in jira_board_id.
    private static (string? ProjectKey, long? BoardId) BoardOrProject(string? value) {
        var text = value?.Trim() ?? "";
        if (long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var board) && board > 0) {
            return (null, board);
        }
        var key = text.ToUpperInvariant();
        return (JiraKey().IsMatch(key) ? key : null, null);
    }

    private static string MaskEmail(string email) {
        var at = email.LastIndexOf('@');
        return at <= 0 ? "•••" : $"{email[0]}•••{email[at..]}";
    }

    [GeneratedRegex(@"^[A-Z][A-Z0-9_]{0,19}\z", RegexOptions.CultureInvariant)]
    private static partial Regex JiraKey();
}

/// <summary>
/// <c>team_members</c> → <c>team_members</c>. Rows whose user was not imported are skipped (and counted by the actor
/// rule). Merged duplicate accounts collapse to the highest role and the earliest join. Then every team gets exactly
/// one owner: none → the creator if a member, else the oldest admin, else the oldest member; several → the earliest
/// keeps it and the rest become admins. A team with no imported member has no owner and is listed.
/// </summary>
public sealed class TeamMembersMap : IImportStage {
    private const int MemberRole = 0;
    private const int AdminRole = 1;
    private const int OwnerRole = 2;

    private static readonly string[] Roles = ["member", "admin", "owner"];

    public int Order => 210;

    public string Name => "team members";

    public async Task RunAsync(ImportContext context, CancellationToken cancellationToken) {
        var tally = context.Report.Table("public.team_members");
        var creators = (await context.Source.ReadAsync("public.teams", cancellationToken))
            .ToDictionary(x => x.Uuid("id")!.Value, x => context.Principals.User(x.Uuid("creator_id")));
        var members = new Dictionary<(Guid Team, Guid User), Member>();
        foreach (var row in await context.Source.ReadAsync("public.team_members", cancellationToken)) {
            var team = row.Uuid("team_id")!.Value;
            if (context.UserRef("public.team_members", "user_id", row.Uuid("user_id")) is not { } user) {
                tally.Skip("no_account");
                continue;
            }
            if (!creators.ContainsKey(team)) {
                tally.Skip("no_team");
                continue;
            }

            var member = new Member(user, Rank(row.Text("role")), row.Timestamp("joined_at") ?? context.ImportedAt);
            if (members.TryGetValue((team, user), out var existing)) {
                tally.Skip("merged_duplicate");
                members[(team, user)] = existing with {
                    Role = Math.Max(existing.Role, member.Role),
                    JoinedAt = existing.JoinedAt < member.JoinedAt ? existing.JoinedAt : member.JoinedAt,
                };
            } else {
                members[(team, user)] = member;
                tally.Imported++;
            }
        }

        var rows = new List<object?[]>();
        foreach (var team in members.GroupBy(x => x.Key.Team, x => x.Value).OrderBy(x => x.Key)) {
            var roster = RepairOwner(context, team.Key, creators[team.Key], [.. team.OrderBy(x => x.JoinedAt).ThenBy(x => x.User)]);
            rows.AddRange(roster.Select(x => new object?[] { team.Key, x.User, Roles[x.Role], x.JoinedAt }));
        }
        var staffed = members.Keys.Select(x => x.Team).ToHashSet();
        foreach (var team in creators.Keys.Where(x => !staffed.Contains(x)).Order()) {
            context.Report.Note("teamsWithoutMembers", team.ToString());
        }

        await TargetRows.CopyAsync(context.Target, "team_members", [
            ("team_id", NpgsqlDbType.Uuid), ("user_id", NpgsqlDbType.Uuid), ("role", NpgsqlDbType.Text), ("joined_at", NpgsqlDbType.TimestampTz),
        ], rows, cancellationToken);
    }

    private static int Rank(string? role) => role switch {
        "owner" => OwnerRole,
        "admin" => AdminRole,
        _ => MemberRole,
    };

    // The roster is ordered oldest first.
    private static List<Member> RepairOwner(ImportContext context, Guid team, Guid? creator, List<Member> roster) {
        var owners = roster.Where(x => x.Role == OwnerRole).ToList();
        if (owners.Count == 1) {
            return roster;
        }

        if (owners.Count > 1) {
            foreach (var demoted in owners.Skip(1)) {
                context.Report.Note("demotedOwners", new JsonObject { ["team"] = team.ToString(), ["user"] = demoted.User.ToString() });
            }
            var kept = owners[0];
            return [.. roster.Select(x => x.Role == OwnerRole && x != kept ? x with { Role = AdminRole } : x)];
        }

        var (owner, reason) = roster.FirstOrDefault(x => x.User == creator) is { } byCreator ? (byCreator, "creator")
            : roster.FirstOrDefault(x => x.Role == AdminRole) is { } admin ? (admin, "oldest_admin")
            : (roster[0], "oldest_member");
        context.Report.Note("ownerRepairs", new JsonObject {
            ["team"] = team.ToString(),
            ["user"] = owner.User.ToString(),
            ["reason"] = reason,
        });
        return [.. roster.Select(x => x == owner ? x with { Role = OwnerRole } : x)];
    }

    private sealed record Member(Guid User, int Role, DateTime JoinedAt);
}
