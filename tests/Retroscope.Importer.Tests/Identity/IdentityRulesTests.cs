using System.Text;
using System.Text.Json.Nodes;
using Npgsql;
using Retroscope.Core.Security;
using Retroscope.Importer.Map;
using Retroscope.Importer.Tests.Infrastructure;

namespace Retroscope.Importer.Tests.Identity;

[Trait("Category", "Identity")]
[Collection(ImporterPostgres.Name)]
public sealed class IdentityRulesTests(ImporterDatabases databases) {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private const string Alice = "00000000-0000-0000-0000-0000000000a1";
    private const string Bob = "00000000-0000-0000-0000-0000000000a2";
    private const string DupLoser = "00000000-0000-0000-0000-0000000000a3";
    private const string DupKept = "00000000-0000-0000-0000-0000000000a4";
    private const string PhoneOnly = "00000000-0000-0000-0000-0000000000a6";
    private const string Guest = "00000000-0000-0000-0000-0000000000a7";

    [Fact]
    public async Task Imported_accounts_are_unverified_unless_a_Google_identity_has_the_same_email() {
        var import = await IdentityImport.CreateAsync(databases, "accounts");

        await import.RunAsync(Ct);

        Assert.Equal([Alice, DupKept], await ColumnAsync(import, "select id::text from users where email_verified_at is not null order by id"));
        Assert.Equal(import.ImportedAt, await import.Harness.TargetScalarAsync<DateTime>($"select email_verified_at from users where id = '{Alice}'"));
        Assert.Equal(["google-alice", "google-bob", "google-dup"], await ColumnAsync(import, "select subject from external_identities order by subject"));
    }

    [Fact]
    public async Task Duplicate_emails_merge_into_one_user_and_the_losers_are_archived_with_their_password_sealed() {
        var import = await IdentityImport.CreateAsync(databases, "accounts");
        var settings = import.Settings with { SealPublicKeyPem = ImportHarness.SealPublicKeyPem };
        var runner = new Importer.Source.ImportRunner(settings, TextWriter.Null, TextReader.Null) {
            Stages = IdentityImport.Stages(),
            Checks = [],
        };

        var report = await runner.RunAsync(new Importer.Source.RunOptions(), Ct);

        Assert.Equal([DupKept], await ColumnAsync(import, "select id::text from users where email = 'dup@example.com'"));
        Assert.Equal("dupper", await import.Harness.TargetScalarAsync<string>($"select nickname from users where id = '{DupKept}'"));
        var loser = JsonNode.Parse(await import.Harness.TargetScalarAsync<string>(
            $"select row::text from archive.rows where source_table = 'auth.users' and source_pk = '{DupLoser}'"))!;
        var wrappedKey = Convert.FromBase64String(report.Fact("seal")!["wrappedKey"]!.GetValue<string>());
        Assert.True(Sealer.TryOpen(ImportHarness.SealKey, wrappedKey, "auth.users", DupLoser, "encrypted_password",
            loser["encrypted_password"]!["sealed"]!.GetValue<string>(), out var password));
        Assert.StartsWith("$2b$10$", Encoding.UTF8.GetString(password), StringComparison.Ordinal);
        Assert.Equal(2, report.Table("auth.users").Archived);
        Assert.Equal(2, report.Findings("mergedAccounts").Count);
    }

    [Fact]
    public async Task Phone_only_accounts_are_skipped_and_anonymous_accounts_become_scrubbed_guests() {
        var import = await IdentityImport.CreateAsync(databases, "accounts");

        var report = await import.RunAsync(Ct);

        Assert.Equal(1, report.Table("auth.users").Skipped["phone_only"]);
        Assert.Equal(PhoneOnly, Assert.Single(report.Findings("skippedPhoneOnly"))!.GetValue<string>());
        Assert.Equal(0L, await import.Harness.TargetScalarAsync<long>($"select count(*) from principals where id = '{PhoneOnly}'"));
        Assert.Equal(0L, await import.Harness.TargetScalarAsync<long>($"select count(*) from external_identities where user_id = '{PhoneOnly}'"));
        Assert.Matches("^Guest-[0-9a-f]{4}$", await import.Harness.TargetScalarAsync<string>(
            $"select display_name from principals where id = '{Guest}' and kind = 'guest'"));
    }

    [Fact]
    public async Task Identity_data_the_parser_refuses_skips_that_identity_and_is_counted() {
        var import = await IdentityImport.CreateAsync(databases, "accounts");
        await import.Harness.SourceSqlAsync($$"""
            insert into auth.identities (id, provider_id, user_id, identity_data, provider, created_at) values
              ('00000000-0000-0000-0000-0000000000bf', 'google-bob-deep', '{{Bob}}',
               ('{"email": "bob@example.com", "deep": ' || repeat('[', 100) || repeat(']', 100) || '}')::jsonb, 'google', '2025-01-03');
            """);

        var report = await import.RunAsync(Ct);

        Assert.Equal(1, report.Table("auth.identities").Skipped["malformed_identity_data"]);
        Assert.Equal(0L, await import.Harness.TargetScalarAsync<long>("select count(*) from external_identities where subject = 'google-bob-deep'"));
        Assert.Null(await import.Harness.TargetScalarAsync<object>($"select email_verified_at from users where id = '{Bob}'") as DateTime?);
    }

    [Fact]
    public async Task Hashes_that_are_not_bcrypt_are_nulled_and_counted() {
        var import = await IdentityImport.CreateAsync(databases, "accounts");

        var report = await import.RunAsync(Ct);

        Assert.Equal(1, report.Counter("nulledPasswordHashes"));
        Assert.Null(await import.Harness.TargetScalarAsync<object>($"select password_hash from users where id = '{Bob}'") as string);
        Assert.Equal("$2a$10$8DTcmYg2wCKWx68MnzgFCOVgQsqU65NL2jKJWT1iAQdXWbz.KiFzi",
            await import.Harness.TargetScalarAsync<string>($"select password_hash from users where id = '{Alice}'"));
    }

    [Fact]
    public async Task Admin_comes_only_from_the_operator_list_and_self_granted_roles_are_listed() {
        var import = await IdentityImport.CreateAsync(databases, "accounts");

        var report = await import.RunAsync(Ct);

        Assert.Equal([Alice], await ColumnAsync(import, "select id::text from users where is_admin"));
        Assert.Equal(Bob, Assert.Single(report.Findings("ignoredAdminRoles"))!["id"]!.GetValue<string>());
    }

    [Fact]
    public async Task Dangling_actor_ids_become_null_and_are_counted() {
        var import = await IdentityImport.CreateAsync(databases, "teams");

        var report = await import.RunAsync(Ct);

        Assert.Equal(0L, await import.Harness.TargetScalarAsync<long>(
            "select count(*) from teams where id = '00000000-0000-0000-0000-0000000000c2' and created_by is not null"));
        Assert.Equal(1, report.Counter("nulledActorRefs.public.teams.creator_id"));
        Assert.Equal(1, report.Counter("nulledActorRefs.public.team_members.user_id"));
        Assert.Equal(1, report.Counter("nulledActorRefs.public.endorsements.to_user_id"));
        Assert.Equal(1, report.Table("public.team_members").Skipped["no_account"]);
    }

    [Fact]
    public async Task Every_team_with_members_ends_with_exactly_one_owner() {
        var import = await IdentityImport.CreateAsync(databases, "teams");

        var report = await import.RunAsync(Ct);

        Assert.Equal(
            [
                "00000000-0000-0000-0000-0000000000c1 " + Alice,
                "00000000-0000-0000-0000-0000000000c2 " + Alice,
                "00000000-0000-0000-0000-0000000000c3 " + Alice,
                "00000000-0000-0000-0000-0000000000c4 " + DupKept,
            ],
            await ColumnAsync(import, "select team_id || ' ' || user_id from team_members where role = 'owner' order by team_id"));
        Assert.Equal("admin", await import.Harness.TargetScalarAsync<string>(
            $"select role from team_members where team_id = '00000000-0000-0000-0000-0000000000c3' and user_id = '{Bob}'"));
        Assert.Equal("admin", await import.Harness.TargetScalarAsync<string>(
            $"select role from team_members where team_id = '00000000-0000-0000-0000-0000000000c3' and user_id = '{DupKept}'"));
        Assert.Equal(["oldest_admin", "oldest_member"], report.Findings("ownerRepairs").Select(x => x!["reason"]!.GetValue<string>()));
        Assert.Single(report.Findings("demotedOwners"));
    }

    [Fact]
    public async Task Merged_accounts_resolve_every_primary_key_collision_by_its_rule() {
        var import = await IdentityImport.CreateAsync(databases, "teams");

        var report = await import.RunAsync(Ct);

        // Team c3: the loser joined first as a member, the kept account later as an admin.
        Assert.Equal("admin 2025-01-10", await import.Harness.TargetScalarAsync<string>(
            $"select role || ' ' || to_char(joined_at at time zone 'UTC', 'YYYY-MM-DD') from team_members where team_id = '00000000-0000-0000-0000-0000000000c3' and user_id = '{DupKept}'"));
        Assert.Equal(0L, await import.Harness.TargetScalarAsync<long>($"select count(*) from team_members where user_id = '{DupLoser}'"));
        Assert.Equal(1, report.Table("public.team_members").Skipped["merged_duplicate"]);

        Assert.Equal("2025-01-02", await import.Harness.TargetScalarAsync<string>(
            $"select to_char(created_at at time zone 'UTC', 'YYYY-MM-DD') from team_favorites where user_id = '{DupKept}' and team_id = '00000000-0000-0000-0000-0000000000c1'"));
        Assert.Equal(1, report.Table("public.user_favorite_teams").Skipped["merged_duplicate"]);

        Assert.Equal("2025-03-05", await import.Harness.TargetScalarAsync<string>(
            $"select to_char(last_at at time zone 'UTC', 'YYYY-MM-DD') from recent_activity where user_id = '{DupKept}' and entity_id = '00000000-0000-0000-0000-000000000901'"));
        Assert.Equal(1, report.Table("public.user_recent_activity").Skipped["merged_duplicate"]);

        // 702 (loser) and 703 (kept) endorse the same person with the same type on the same board; the older stays.
        // 704 has the loser endorse the kept account, which the merge turns into a self-endorsement.
        Assert.Equal(
            ["00000000-0000-0000-0000-000000000701", "00000000-0000-0000-0000-000000000702"],
            await ColumnAsync(import, "select id::text from endorsements order by id"));
        Assert.Equal(1, report.Table("public.endorsements").Skipped["duplicate"]);
        Assert.Equal(1, report.Table("public.endorsements").Skipped["self"]);
        Assert.Equal(0L, await import.Harness.TargetScalarAsync<long>("select count(*) from endorsements where from_user_id = to_user_id"));
    }

    [Fact]
    public async Task Jira_tokens_are_sealed_for_the_server_and_marked_rotate_required() {
        var import = await IdentityImport.CreateAsync(databases, "teams");

        var report = await import.RunAsync(Ct);

        await using var connection = new NpgsqlConnection(import.Harness.Target);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(
            "select team_id, site_host, credentials_box, key_id, token_last4, email_masked, board_id, ticket_prefix, status from team_jira_config order by team_id",
            connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        Assert.True(await reader.ReadAsync(Ct));
        var team = reader.GetGuid(0);
        Assert.Equal("acme.atlassian.net", reader.GetString(1));
        Assert.True(import.Keyring.TryOpen(KeyPurpose.JiraCredentials, (byte[])reader[2], TeamsMap.JiraAssociatedData(team), out var plaintext));
        Assert.Equal("""{"email":"jira@acme.com","token":"secret-token-1234"}""", Encoding.UTF8.GetString(plaintext));
        Assert.False(import.Keyring.TryOpen(KeyPurpose.JiraCredentials, (byte[])reader[2], TeamsMap.JiraAssociatedData(Guid.NewGuid()), out _));
        Assert.Equal("k1", reader.GetString(3));
        Assert.Equal("1234", reader.GetString(4));
        Assert.Equal("j•••@acme.com", reader.GetString(5));
        Assert.Equal(42L, reader.GetInt64(6));
        Assert.Equal("ALP", reader.GetString(7));
        Assert.Equal("rotate_required", reader.GetString(8));
        Assert.True(await reader.ReadAsync(Ct));
        Assert.Equal("delta.atlassian.net", reader.GetString(1));
        Assert.False(await reader.ReadAsync(Ct));
        Assert.Equal(["host", "email"], report.Findings("jiraConfigsRejected").Select(x => x!["reason"]!.GetValue<string>()));
        Assert.Equal(2, report.Findings("jiraRotateRequired").Count);
    }

    [Fact]
    public async Task Link_invites_are_capped_to_seven_days_and_only_ever_make_members() {
        var import = await IdentityImport.CreateAsync(databases, "invites");
        await import.Harness.SourceSqlAsync("""
            insert into public.team_invitations (id, team_id, email, invited_by, token, status, expires_at, created_at, invite_type, is_active)
            values ('00000000-0000-0000-0000-000000000599', '00000000-0000-0000-0000-0000000000c1', 'soon@invite',
                    '00000000-0000-0000-0000-0000000000a1', 'link-token-soon', 'pending', now() + interval '2 days', now(), 'link', true);
            """);

        var report = await import.RunAsync(Ct);

        Assert.Equal(["link member"], await ColumnAsync(import, "select distinct kind || ' ' || role from team_invites where kind = 'link'"));
        Assert.Equal(import.ImportedAt.AddDays(7), await import.Harness.TargetScalarAsync<DateTime>(
            "select expires_at from team_invites where id = '00000000-0000-0000-0000-000000000502'"));
        var soon = await import.Harness.TargetScalarAsync<DateTime>("select expires_at from team_invites where id = '00000000-0000-0000-0000-000000000599'");
        Assert.InRange(soon, import.ImportedAt.AddDays(1), import.ImportedAt.AddDays(3));
        Assert.Single(report.Findings("cappedLinkInvites"));

        var box = await import.Harness.TargetScalarAsync<byte[]>("select token_box from team_invites where id = '00000000-0000-0000-0000-000000000502'");
        Assert.True(import.Keyring.TryOpen(KeyPurpose.InviteBox, box, InvitesMap.InviteAssociatedData(new Guid("00000000-0000-0000-0000-000000000502")), out var token));
        Assert.Equal("link-token-1", Encoding.UTF8.GetString(token));
        Assert.Equal(TokenHash.Of("link-token-1"), await import.Harness.TargetScalarAsync<byte[]>(
            "select token_hash from team_invites where id = '00000000-0000-0000-0000-000000000502'"));
    }

    [Fact]
    public async Task Invitations_that_cannot_be_accepted_are_archived_with_their_token_sealed() {
        var import = await IdentityImport.CreateAsync(databases, "invites");

        var report = await import.RunAsync(Ct);

        Assert.Equal(3, report.Table("public.team_invitations").Archived);
        Assert.Equal(["email new.person@example.com", "email other@example.com", "link "],
            await ColumnAsync(import, "select kind || ' ' || coalesce(email, '') from team_invites order by kind, email"));
        Assert.Equal(1, report.Counter("nulledActorRefs.public.team_invitations.invited_by"));
        var archived = await import.Harness.TargetScalarAsync<string>(
            "select string_agg(row::text, ' ') from archive.rows where source_table = 'public.team_invitations'");
        Assert.DoesNotContain("-token", archived, StringComparison.Ordinal);
    }

    private static async Task<List<string>> ColumnAsync(IdentityImport import, string sql) {
        await using var connection = new NpgsqlConnection(import.Harness.Target);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync(Ct);
        var values = new List<string>();
        while (await reader.ReadAsync(Ct)) {
            values.Add(reader.GetString(0));
        }
        return values;
    }
}
