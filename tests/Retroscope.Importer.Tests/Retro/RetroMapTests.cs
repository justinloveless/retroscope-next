using System.Security.Cryptography;
using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Markup;
using Retroscope.Importer.Html;
using Retroscope.Importer.Map;
using Retroscope.Importer.Source;
using Retroscope.Importer.Tests.Infrastructure;
using static Retroscope.Importer.Tests.Retro.RetroFixture;

namespace Retroscope.Importer.Tests.Retro;

[Trait("Category", "RetroMap")]
[Collection(ImporterPostgres.Name)]
public sealed class RetroMapTests(ImporterDatabases databases) {
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Boards_without_an_imported_team_are_skipped_with_everything_on_them() {
        var fixture = await RetroFixture.CreateAsync(databases);

        var report = await fixture.Runner().RunAsync(new RunOptions(), Ct);

        Assert.Equal(
            new[] { PublicBoard, TeamBoard, OldBoard }.Order(StringComparer.Ordinal),
            (await fixture.ScalarAsync<string>("select string_agg(id::text, ',') from retro_boards")).Split(',').Order(StringComparer.Ordinal));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("select count(*) from retro_boards where team_id is null"));
        var boards = report.Table("public.retro_boards");
        Assert.Equal((2L, 1L), (boards.Skipped[RetroMap.NoTeam], boards.Skipped[RetroMap.TeamMissing]));
        foreach (var table in new[] {
            "public.retro_board_config", "public.retro_columns", "public.retro_items", "public.retro_comments", "public.retro_votes",
            "public.retro_user_readiness",
        }) {
            Assert.Equal(1L, report.Table(table).Skipped[RetroMap.NoTeam]);
        }
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            $"select count(*) from principals where id = '{SyntheticIds.Guest(QuickSession)}'"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>($"select count(*) from retro_cards where id = '{QuickCard}'"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>("select count(*) from blobs where board_id <> '" + TeamBoard + "'"));
    }

    [Fact]
    public async Task No_board_password_reaches_the_target_the_report_or_the_archive_in_plain_text() {
        var fixture = await RetroFixture.CreateAsync(databases);

        var report = await fixture.Runner(ImportHarness.SealPublicKeyPem).RunAsync(new RunOptions(), Ct);

        var archived = await fixture.ScalarAsync<string>(
            $"select row::text from archive.rows where source_table = 'public.retro_boards#dropped' and source_pk = '{TeamBoard}'");
        Assert.Contains("sealed", archived, StringComparison.Ordinal);
        Assert.Contains("is_private", archived, StringComparison.Ordinal);
        var everything = await fixture.ScalarAsync<string>("""
            select string_agg(t, ' ') from (
              select row_to_json(b)::text as t from retro_boards b
              union all select row::text from archive.rows) x
            """);
        foreach (var password in new[] { "hunter2", "demo123", new string('a', 64), new string('b', 64) }) {
            Assert.DoesNotContain(password, everything, StringComparison.Ordinal);
            Assert.DoesNotContain(password, report.ToJsonString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Boards_keep_their_codes_and_inline_their_config() {
        var fixture = await RetroFixture.CreateAsync(databases);

        await fixture.Runner().RunAsync(new RunOptions(), Ct);

        Assert.Equal("PUB123|Public retro|thinking|f|t|3|f|t|t|f|🎉🎉🎉🎉🎉🎉🎉🎉|t|idle|300000|f|t", await fixture.ScalarAsync<string>($"""
            select concat_ws('|', code, title, stage, allow_anonymous, voting_enabled, max_votes_per_user, show_author_names, stages_enabled,
                             enforce_readiness, allow_self_votes, vote_emoji, sort_chronologically, timer_state, timer_duration_ms, timer_alarm, timer_music)
            from retro_boards where id = '{PublicBoard}'
            """));
        Assert.Equal($"{OldBoard}|Retro|discussing|∅|👍|900000|2025-02-01|2025-02-01|2025-02-01", await fixture.ScalarAsync<string>($"""
            select concat_ws('|', code, title, stage, coalesce(max_votes_per_user::text, '∅'), vote_emoji, timer_duration_ms,
                             archived_at::date, deleted_at::date, last_activity_at::date)
            from retro_boards where id = '{OldBoard}'
            """));
        Assert.Equal("voting|t|t|900000", await fixture.ScalarAsync<string>(
            $"select concat_ws('|', stage, allow_anonymous, voting_enabled, timer_duration_ms) from retro_boards where id = '{TeamBoard}'"));
        Assert.Equal(Guid.Parse(Alice), await fixture.ScalarAsync<Guid>($"select created_by from retro_boards where id = '{PublicBoard}'"));
        Assert.Equal(Guid.Parse(Team), await fixture.ScalarAsync<Guid>($"select team_id from retro_boards where id = '{PublicBoard}'"));
    }

    [Fact]
    public async Task Columns_get_dense_sort_keys_one_action_items_flag_and_palette_colours() {
        var fixture = await RetroFixture.CreateAsync(databases);

        var report = await fixture.Runner().RunAsync(new RunOptions(), Ct);

        Assert.Equal(
            $"{BadColumn}:0:red:f:Bad,{GoodColumn}:1:green:f:Good,{ActionColumn}:2:yellow:t:Actions,{SecondActionColumn}:3:gray:f:Untitled",
            await fixture.ScalarAsync<string>(
                "select string_agg(concat_ws(':', id, sort_key, color, is_action_items, title), ',' order by sort_key) from retro_columns"));
        Assert.Equal(1, report.Table("public.retro_columns").Skipped["orphan"]);
    }

    [Fact]
    public async Task Authorship_follows_the_anonymity_and_guest_rules() {
        var fixture = await RetroFixture.CreateAsync(databases);

        var report = await fixture.Runner().RunAsync(new RunOptions(), Ct);
        var tagger = fixture.Context!.Keys.AnonTagger;
        var guest = SyntheticIds.Guest(GuestSession);

        Assert.Equal(tagger.Tag(new BoardId(Guid.Parse(TeamBoard)), new PrincipalId(Guid.Parse(Alice))), await AnonTag(fixture, "retro_cards", AnonCard));
        Assert.Equal(tagger.Tag(new BoardId(Guid.Parse(TeamBoard)), new PrincipalId(guest)), await AnonTag(fixture, "retro_cards", AnonGuestCard));
        Assert.Equal(tagger.Tag(new BoardId(Guid.Parse(TeamBoard)), new PrincipalId(Guid.Parse(Bob))), await AnonTag(fixture, "retro_comments", AnonComment));
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "select count(*) from (select author_id, legacy_author from retro_cards where anon_tag is not null union all select author_id, legacy_author from retro_comments where anon_tag is not null) x where author_id is not null or legacy_author is not null"));

        Assert.Equal(Guid.Parse(Alice), await fixture.ScalarAsync<Guid>($"select author_id from retro_cards where id = '{NamedCard}'"));
        Assert.Equal(guest, await fixture.ScalarAsync<Guid>($"select author_id from retro_cards where id = '{GuestCard}'"));
        Assert.Equal(guest, await fixture.ScalarAsync<Guid>($"select author_id from retro_comments where id = '{GuestComment}'"));
        Assert.Equal($"guest|{DisplayNames.Scrub("guest@example.com")}", await fixture.ScalarAsync<string>(
            $"select kind || '|' || display_name from principals where id = '{guest}'"));
        Assert.StartsWith("Guest-", DisplayNames.Scrub("guest@example.com"), StringComparison.Ordinal);
        Assert.Equal("Old Name", await fixture.ScalarAsync<string>($"select legacy_author from retro_cards where id = '{LegacyCard}'"));
        Assert.Equal(Guid.Parse(TeamBoard), await fixture.ScalarAsync<Guid>($"select board_id from retro_cards where id = '{BoardlessCard}'"));
        Assert.Equal(1, report.Table("public.retro_items").Skipped["orphan"]);
        Assert.Equal(1, report.Table("public.retro_comments").Skipped["cardMissing"]);
    }

    [Fact]
    public async Task Base64_images_become_blobs_without_a_creator_when_anonymous() {
        var fixture = await RetroFixture.CreateAsync(databases);

        await fixture.Runner().RunAsync(new RunOptions(), Ct);

        var blobA = HtmlToMarkup.BlobId(Guid.Parse(TeamBoard), SHA256.HashData(PngA));
        var blobB = HtmlToMarkup.BlobId(Guid.Parse(TeamBoard), SHA256.HashData(PngB));
        Assert.Equal(2L, await fixture.ScalarAsync<long>("select count(*) from blobs"));
        Assert.Null(await fixture.ScalarAsync<object>($"select created_by from blobs where id = '{blobA}'") as Guid?);
        Assert.Equal(Guid.Parse(Alice), await fixture.ScalarAsync<Guid>($"select created_by from blobs where id = '{blobB}'"));
        Assert.Equal(PngA, await fixture.ScalarAsync<byte[]>($"select bytes from blobs where id = '{blobA}'"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "select count(*) from blobs where purpose <> 'board_image' or attached_at is null or content_type <> 'image/png'"));

        Assert.Contains($"![image](blob:{blobA})", await fixture.ScalarAsync<string>($"select body from retro_cards where id = '{AnonCard}'"), StringComparison.Ordinal);
        Assert.Contains($"![image](blob:{blobA})", await fixture.ScalarAsync<string>($"select text from team_action_items where id = '{AnonAction}'"), StringComparison.Ordinal);

        // §h.5 S6: no anonymous card or comment reaches a blob that names anyone.
        Assert.Equal(0L, await fixture.ScalarAsync<long>("""
            select count(*) from blobs b
            where b.created_by is not null and exists (
              select 1 from (select body from retro_cards where anon_tag is not null or (author_id is null and legacy_author is null)
                             union all select body from retro_comments where anon_tag is not null) a
              where a.body like '%blob:' || b.id || ')%')
            """));
    }

    [Fact]
    public async Task A_blob_keeps_its_creator_when_a_later_body_has_an_unknown_but_not_anonymous_author() {
        var fixture = await RetroFixture.CreateAsync(databases);
        await fixture.Harness.SourceSqlAsync($"""
            insert into public.team_action_items (id, team_id, text, source_board_id, source_item_id, created_at, created_by, done) values
              ('ac710000-0000-4000-8000-0000000000aa', '{Team}', '<p>again {ImageB}</p>', '{TeamBoard}', null, '2026-01-02', 'dead0000-0000-4000-8000-000000000001', false);
            """);

        await fixture.Runner().RunAsync(new RunOptions(), Ct);

        var blobB = HtmlToMarkup.BlobId(Guid.Parse(TeamBoard), SHA256.HashData(PngB));
        Assert.Contains($"![image](blob:{blobB})", await fixture.ScalarAsync<string>(
            "select text from team_action_items where id = 'ac710000-0000-4000-8000-0000000000aa'"), StringComparison.Ordinal);
        Assert.Equal(Guid.Parse(Alice), await fixture.ScalarAsync<Guid>($"select created_by from blobs where id = '{blobB}'"));
    }

    [Fact]
    public async Task Action_items_link_their_card_and_never_name_an_anonymous_author() {
        var fixture = await RetroFixture.CreateAsync(databases);

        var report = await fixture.Runner().RunAsync(new RunOptions(), Ct);

        Assert.Equal($"{AnonCard}|1|{Bob}|∅|∅|∅", await ActionItem(fixture, AnonAction));
        Assert.Equal($"{NamedCard}|1|{Carol}|2026-01-02|{Alice}|{Bob}", await ActionItem(fixture, DoneAction));
        Assert.Equal($"∅|0|∅|∅|∅|{AnonAuth}", await ActionItem(fixture, LooseAction));
        Assert.Equal("no board \\[image removed\\]", await fixture.ScalarAsync<string>($"select text from team_action_items where id = '{LooseAction}'"));
        Assert.Null(await fixture.ScalarAsync<object>($"select source_board_id from team_action_items where id = '{LooseAction}'") as Guid?);
        Assert.Equal(1, report.Table("public.team_action_items").Skipped["teamMissing"]);
        Assert.Equal(1, report.Counter("nulledActorRefs.team_action_items.assigned_to"));
        Assert.Equal(1, report.Counter("html.public.team_action_items.imageWithoutBoard"));
    }

    [Fact]
    public async Task Mentions_are_kept_as_tokens_and_recorded_for_team_members_only() {
        var fixture = await RetroFixture.CreateAsync(databases);

        await fixture.Runner().RunAsync(new RunOptions(), Ct);

        Assert.Equal($"card:{AnonCard}:{Bob}", await fixture.ScalarAsync<string>(
            "select string_agg(concat_ws(':', source_kind, source_id, user_id), ',') from mentions"));
        Assert.Contains($"[[mention:{Carol}:Carol]]", await fixture.ScalarAsync<string>($"select body from retro_cards where id = '{NamedCard}'"), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_imported_body_passes_the_DocNode_invariant() {
        var fixture = await RetroFixture.CreateAsync(databases);

        await fixture.Runner().RunAsync(new RunOptions(), Ct);

        Assert.Equal("<div>pasted</div>x", await fixture.ScalarAsync<string>($"select body from retro_cards where id = '{XssCard}'"));
        var bodies = new List<(string Body, MarkupField Field)>();
        await using var connection = new NpgsqlConnection(fixture.Harness.Target);
        await connection.OpenAsync(Ct);
        await using var command = new NpgsqlCommand("""
            select body, 0 from retro_cards union all select body, 1 from retro_comments union all select text, 2 from team_action_items
            """, connection);
        await using (var reader = await command.ExecuteReaderAsync(Ct)) {
            while (await reader.ReadAsync(Ct)) {
                bodies.Add((reader.GetString(0), reader.GetInt32(1) switch { 0 => MarkupField.Card, 1 => MarkupField.Comment, _ => MarkupField.ActionItem }));
            }
        }

        Assert.Equal(12, bodies.Count);
        foreach (var (body, field) in bodies) {
            var check = MarkupLimits.Check(body, field);
            Assert.True(check.IsValid, body);
            Assert.Equal(body, MarkupWriter.Write(check.Doc!));
            Assert.All(check.Doc!.Descendants(), x => Assert.True(
                x is DocParagraph or DocBulletList or DocOrderedList or DocListItem or DocQuote or DocCodeBlock or DocText
                    or DocStyled { Style: not DocStyle.Underline } or DocCode or DocLink or DocLineBreak or DocMention or DocImage,
                x.GetType().Name));
            Assert.DoesNotContain("javascript:", body, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Votes_collapse_on_card_and_voter_and_the_recount_is_reported() {
        var fixture = await RetroFixture.CreateAsync(databases);

        var report = await fixture.Runner().RunAsync(new RunOptions(), Ct);

        var voterGuest = SyntheticIds.Guest(VoterSession);
        Assert.Equal($"{voterGuest},{Alice}", await fixture.ScalarAsync<string>(
            $"select string_agg(voter_id::text, ',' order by voter_id::text) from retro_votes where card_id = '{NamedCard}'"));
        Assert.Equal(0L, await fixture.ScalarAsync<long>(
            "select count(*) from retro_votes v join retro_cards c on c.id = v.card_id where v.board_id <> c.board_id"));
        Assert.Equal(Guid.Parse(TeamBoard), await fixture.ScalarAsync<Guid>($"select board_id from retro_votes where card_id = '{GuestCard}'"));
        Assert.Equal("Guest", await fixture.ScalarAsync<string>($"select display_name from principals where id = '{voterGuest}'"));

        var tally = report.Table("public.retro_votes");
        Assert.Equal(1, tally.Skipped["duplicate"]);
        Assert.Equal(1, tally.Skipped["cardMissing"]);
        var recount = Assert.Single(report.Findings("voteRecount"));
        Assert.Equal(NamedCard, recount!["card"]!.GetValue<string>());
        Assert.Equal(3, recount["source"]!.GetValue<int>());
        Assert.Equal(2, recount["imported"]!.GetValue<int>());
        Assert.Equal(1, report.Counter("retroVotes.recountDrift"));
    }

    [Fact]
    public async Task Readiness_for_the_current_stage_is_imported_and_the_rest_archived() {
        var fixture = await RetroFixture.CreateAsync(databases);

        await fixture.Runner().RunAsync(new RunOptions(), Ct);

        Assert.Equal($"{TeamBoard}:voting:{Alice}", await fixture.ScalarAsync<string>(
            "select string_agg(concat_ws(':', board_id, stage, principal_id), ',') from retro_readiness"));
        Assert.Equal(2L, await fixture.ScalarAsync<long>("select count(*) from archive.rows where source_table = 'public.retro_user_readiness'"));
    }

    [Fact]
    public async Task Every_source_row_is_imported_skipped_or_archived() {
        var fixture = await RetroFixture.CreateAsync(databases);

        var report = await fixture.Runner().RunAsync(new RunOptions(), Ct);

        foreach (var table in RetroTables) {
            var tally = report.Table(table);
            Assert.True(tally.Source > 0, table);
            Assert.Equal(tally.Source, tally.Imported + tally.Skipped.Values.Sum() + tally.Archived);
        }
        Assert.Equal(3, report.Table("public.retro_boards").Imported);
        Assert.Equal(7, report.Table("public.retro_items").Imported);
    }

    private static Task<byte[]> AnonTag(RetroFixture fixture, string table, string id) =>
        fixture.ScalarAsync<byte[]>($"select anon_tag from {table} where id = '{id}'");

    private static Task<string> ActionItem(RetroFixture fixture, string id) => fixture.ScalarAsync<string>($"""
        select concat_ws('|', coalesce(source_card_id::text, '∅'), source_version, coalesce(assigned_to::text, '∅'),
                         coalesce(done_at::date::text, '∅'), coalesce(done_by::text, '∅'), coalesce(created_by::text, '∅'))
        from team_action_items where id = '{id}'
        """);
}
