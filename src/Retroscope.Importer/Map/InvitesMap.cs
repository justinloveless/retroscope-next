using System.Text;
using System.Text.Json.Nodes;
using NpgsqlTypes;
using Retroscope.Core.Security;
using Retroscope.Importer.Source;

namespace Retroscope.Importer.Map;

/// <summary>
/// <c>team_invitations</c> → <c>team_invites</c> (DESIGN §h.2). Only pending, active, unexpired invitations carry over,
/// stored as <c>sha256(token)</c> so old invite links keep working. Imported invite tokens are treated as exposed (S10), so a
/// link invite expires within 7 days of the import and never grants more than member. Everything else is archived
/// with its token sealed.
/// </summary>
public sealed class InvitesMap : IImportStage {
    public static readonly TimeSpan LinkInviteCap = TimeSpan.FromDays(7);

    public int Order => 240;

    public string Name => "team invites";

    public async Task RunAsync(ImportContext context, CancellationToken cancellationToken) {
        var teams = (await context.Source.ReadAsync("public.teams", cancellationToken)).Select(x => x.Uuid("id")!.Value).ToHashSet();
        var tally = context.Report.Table("public.team_invitations");
        var tokens = new HashSet<string>(StringComparer.Ordinal);
        var rows = new List<object?[]>();
        foreach (var invitation in await context.Source.ReadAsync("public.team_invitations", cancellationToken)) {
            if (!IsLive(invitation, context.ImportedAt) || !teams.Contains(invitation.Uuid("team_id")!.Value)) {
                context.Archive.AddRow(invitation);
                tally.Archived++;
                continue;
            }

            var token = invitation.Text("token");
            var email = PrincipalDirectory.NormalizeEmail(invitation.Text("email"));
            var isLink = invitation.Text("invite_type") == "link";
            if (string.IsNullOrEmpty(token) || !tokens.Add(token)) {
                tally.Skip("duplicate_token");
            } else if (!isLink && email is null) {
                tally.Skip("no_email");
            } else {
                rows.Add(isLink ? Link(context, invitation, token) : Email(context, invitation, token, email!));
                tally.Imported++;
            }
        }

        await TargetRows.CopyAsync(context.Target, "team_invites", [
            ("id", NpgsqlDbType.Uuid), ("team_id", NpgsqlDbType.Uuid), ("kind", NpgsqlDbType.Text), ("email", NpgsqlDbType.Text),
            ("role", NpgsqlDbType.Text), ("token_hash", NpgsqlDbType.Bytea), ("token_box", NpgsqlDbType.Bytea), ("created_by", NpgsqlDbType.Uuid),
            ("created_at", NpgsqlDbType.TimestampTz), ("expires_at", NpgsqlDbType.TimestampTz),
        ], rows, cancellationToken);
    }

    /// <summary>
    /// The associated data of <c>team_invites.token_box</c>: the invite id's 16 bytes in RFC 4122 order, the same layout
    /// the Jira box and anonymous tags use for their ids.
    /// </summary>
    public static byte[] InviteAssociatedData(Guid invite) {
        var aad = new byte[16];
        invite.TryWriteBytes(aad, bigEndian: true, out _);
        return aad;
    }

    private static bool IsLive(SourceRow invitation, DateTime now) =>
        invitation.Text("status") == "pending" && invitation.Bool("is_active") == true && invitation.Timestamp("expires_at") > now;

    // The source has no role column: every invitation made a member, so email invites import as member too.
    private static object?[] Email(ImportContext context, SourceRow invitation, string token, string email) => [
        invitation.Uuid("id")!.Value,
        invitation.Uuid("team_id")!.Value,
        "email",
        email,
        "member",
        TokenHash.Of(token),
        null,
        CreatedBy(context, invitation),
        invitation.Timestamp("created_at") ?? context.ImportedAt,
        invitation.Timestamp("expires_at")!.Value,
    ];

    private static object?[] Link(ImportContext context, SourceRow invitation, string token) {
        var id = invitation.Uuid("id")!.Value;
        var expiresAt = invitation.Timestamp("expires_at")!.Value;
        var cap = context.ImportedAt + LinkInviteCap;
        if (expiresAt > cap) {
            context.Report.Note("cappedLinkInvites", new JsonObject { ["id"] = id.ToString(), ["team"] = invitation.Uuid("team_id")!.Value.ToString() });
            expiresAt = cap;
        }
        return [
            id,
            invitation.Uuid("team_id")!.Value,
            "link",
            null,
            "member",
            TokenHash.Of(token),
            context.Keys.Keyring.Seal(KeyPurpose.InviteBox, Encoding.UTF8.GetBytes(token), InviteAssociatedData(id)),
            CreatedBy(context, invitation),
            invitation.Timestamp("created_at") ?? context.ImportedAt,
            expiresAt,
        ];
    }

    private static Guid? CreatedBy(ImportContext context, SourceRow invitation) =>
        context.UserRef("public.team_invitations", "invited_by", invitation.Uuid("invited_by"));
}
