using Retroscope.Importer.Archive;
using Retroscope.Importer.Map;
using Retroscope.Importer.Source;
using Retroscope.Importer.Tests.Infrastructure;
using static Retroscope.Importer.Tests.Poker.PokerFixture;

namespace Retroscope.Importer.Tests.Poker;

[Trait("Category", "PokerMap")]
[Collection(ImporterPostgres.Name)]
public sealed class PokerMapTests(ImporterDatabases databases) {
    private static readonly Guid Session = new("5e000000-0000-0000-0000-000000000001");

    [Fact]
    public async Task Duplicate_round_numbers_keep_the_latest_and_renumber_the_others_above_the_max() {
        var fixture = await CreateAsync(databases);
        var seed = Rounds(
                Round(R(1), Session, 1, createdHours: 0, sortOrder: 0),
                Round(R(2), Session, 1, createdHours: 1, sortOrder: 1),
                Round(R(3), Session, 2, createdHours: 2, sortOrder: 2),
                Round(R(4), Session, 0, createdHours: 3, sortOrder: 3),
                Round(R(5), Session, 5, createdHours: 4, sortOrder: 4, pending: true));
        await fixture.SourceSqlAsync($"""
            insert into public.poker_sessions (id, team_id, room_id, current_round_number, created_at) values
              ('{Session}', '{TeamId}', 'ABC123', 2, '{At(0)}');
            {seed}
            """);

        var report = await fixture.RunAsync();

        var rounds = await fixture.RowsAsync("select id, number, sort_key from poker_rounds order by sort_key");
        Assert.Equal(new[] { (R(1), 3, 0), (R(2), 1, 1), (R(3), 2, 2), (R(4), 4, 3) },
            rounds.Select(x => ((Guid)x["id"]!, (int)x["number"]!, (int)x["sort_key"]!)));
        var session = await fixture.RowAsync("select code, team_id, current_round_id, next_round_number from poker_sessions");
        Assert.Equal("ABC123", session["code"]);
        Assert.Equal(TeamId, session["team_id"]);
        Assert.Equal(R(3), session["current_round_id"]);
        Assert.Equal(5, session["next_round_number"]);
        Assert.Equal(new[] { $"{R(1)}:1→3", $"{R(4)}:0→4" },
            report.Findings("pokerRenumberedRounds").Select(x => $"{x!["round"]}:{x["from"]}→{x["to"]}").Order(StringComparer.Ordinal));
        Assert.Equal(4, report.Table(PokerMap.RoundsTable).Imported);
        Assert.Equal(1, report.Table(PokerMap.RoundsTable).Skipped["pending"]);
        Assert.Equal(1, report.Table(PokerMap.SessionsTable).Imported);
    }

    [Fact]
    public async Task Pending_rounds_are_skipped_and_never_become_the_pointer() {
        var fixture = await CreateAsync(databases);
        var seed = Rounds(
                Round(R(1), Session, 1, createdHours: 0, sortOrder: 0),
                Round(R(2), Session, 2, createdHours: 1, sortOrder: 1, pending: true));
        await fixture.SourceSqlAsync($"""
            insert into public.poker_sessions (id, room_id, current_round_number, created_at) values ('{Session}', 'quick1', 2, '{At(0)}');
            {seed}
            """);

        var report = await fixture.RunAsync();

        Assert.Equal(R(1), Assert.Single(await fixture.RowsAsync("select id from poker_rounds"))["id"]);
        var session = await fixture.RowAsync("select team_id, current_round_id, next_round_number from poker_sessions");
        Assert.Null(session["team_id"]);
        Assert.Equal(R(1), session["current_round_id"]);
        Assert.Equal(2, session["next_round_number"]);
        Assert.Equal(1, report.Counter("poker.pointerRepaired"));
        Assert.Equal(1, report.Table(PokerMap.RoundsTable).Skipped["pending"]);
    }

    [Fact]
    public async Task Slack_sessions_import_as_team_sessions_with_locked_votes_from_external_principals() {
        var fixture = await CreateAsync(databases);
        var seed = Rounds(
                Round(R(1), Session, 1, createdHours: 0, sortOrder: 1, gameState: "Playing", average: 8, selections: """
                    {"slack_1a2b": {"points": 8, "display_name": "Ann Smith"}, "slack_ff": {"points": 5, "display_name": "Bob"},
                     "slack_99": {"points": 8, "display_name": "Cy"}}
                    """),
                Round(R(2), Session, 2, createdHours: 1, sortOrder: 2, selections: """
                    {"slack_1a2b": {"points": 5, "display_name": "Ann Smith"}, "slack_ff": {"points": -1, "display_name": "Bob"}}
                    """));
        await fixture.SourceSqlAsync($"""
            insert into public.poker_sessions (id, team_id, room_id, current_round_number, created_at, send_to_slack) values
              ('{Session}', '{TeamId}', '{TeamId}', 2, '{At(0)}', true);
            {seed}
            """);

        await fixture.RunAsync();

        var ann = SyntheticIds.Slack("slack_1a2b");
        var session = await fixture.RowAsync("select code, team_id from poker_sessions");
        Assert.Equal(TeamId.ToString(), session["code"]);
        Assert.Equal(TeamId, session["team_id"]);
        var principals = await fixture.RowsAsync("select id, kind, display_name from principals where kind <> 'user' order by id");
        Assert.Equal(3, principals.Count);
        Assert.All(principals, x => Assert.Equal(("external", PokerMap.SlackDisplayName), ((string)x["kind"]!, (string)x["display_name"]!)));
        Assert.Contains(principals, x => (Guid)x["id"]! == ann);

        var revealed = await fixture.RowAsync($"select state, winner_points from poker_rounds where id = '{R(1)}'");
        Assert.Equal(("revealed", (short)8), ((string)revealed["state"]!, (short)revealed["winner_points"]!));
        var voting = await VotesAsync(fixture, R(2));
        Assert.Equal(
            new[] { $"{ann}:5:locked", $"{SyntheticIds.Slack("slack_ff")}:abstained:locked" }.Order(StringComparer.Ordinal),
            voting.Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task Unrevealed_rounds_keep_only_locked_deck_votes() {
        var fixture = await CreateAsync(databases);
        var guestDefault = Guid.Parse("9a000000-0000-0000-0000-000000000001");
        var guestPlaceholder = Guid.Parse("9a000000-0000-0000-0000-000000000002");
        var guestAbstain = Guid.Parse("9a000000-0000-0000-0000-000000000003");
        var seed = Rounds(Round(R(1), Session, 1, createdHours: 0, sortOrder: 0, selections: $$$"""
                {"{{{UserA}}}": {"points": 5, "locked": true, "name": "A"},
                 "{{{guestDefault}}}": {"points": 1, "locked": false, "name": "Default"},
                 "{{{guestPlaceholder}}}": {"points": 0, "locked": false, "name": "Placeholder"},
                 "{{{guestAbstain}}}": {"points": -1, "locked": true, "name": "Abstainer"},
                 "{{{UserB}}}": {"points": 3, "betweenHighPoints": 8, "locked": true, "name": "B"},
                 "local-key": {"points": 13, "betweenHighPoints": 21, "locked": true, "name": "Zed"},
                 "{{{Observer}}}": {"points": 2, "locked": true, "name": "Watcher"},
                 "broken": 7}
                """));
        await fixture.SourceSqlAsync($"""
            insert into public.poker_sessions (id, room_id, current_round_number, created_at, observer_ids) values
              ('{Session}', 'quick1', 1, '{At(0)}', '{"{" + Observer + "}"}');
            {seed}
            """);

        var report = await fixture.RunAsync();

        var round = await fixture.RowAsync("select state, winner_points, revealed_at from poker_rounds");
        Assert.Equal("voting", round["state"]);
        Assert.Null(round["winner_points"]);
        Assert.Null(round["revealed_at"]);
        var abstainer = SyntheticIds.Guest(guestAbstain.ToString());
        var zed = SyntheticIds.Guest("local-key");
        Assert.Equal(
            new[] { $"{UserA}:5:locked", $"{abstainer}:abstained:locked", $"{UserB}:3:locked", $"{zed}:13-21:locked" }.Order(StringComparer.Ordinal),
            (await VotesAsync(fixture, R(1))).Order(StringComparer.Ordinal));
        Assert.Equal(2, report.Counter("poker.droppedSelections.unlocked"));
        Assert.Equal(1, report.Counter("poker.droppedSelections.observer"));
        Assert.Equal(1, report.Counter("poker.droppedSelections.malformed"));
        Assert.Equal(1, report.Counter("poker.droppedHighPoints"));
        Assert.Equal(Observer, (await fixture.RowAsync("select principal_id from poker_observers"))["principal_id"]);
        var guests = await fixture.RowsAsync("select id, display_name from principals where kind = 'guest'");
        Assert.Equal(new[] { abstainer, zed }.Order(), guests.Select(x => (Guid)x["id"]!).Order());
    }

    [Fact]
    public async Task Revealed_rounds_keep_every_entry_and_recompute_the_winner() {
        var fixture = await CreateAsync(databases);
        var guest = Guid.Parse("9a000000-0000-0000-0000-000000000001");
        var between = Guid.Parse("9a000000-0000-0000-0000-000000000002");
        var seed = Rounds(
                Round(R(1), Session, 1, createdHours: 0, sortOrder: 0, gameState: "Playing", average: 5, completedHours: 0.5, selections: $$$"""
                    {"{{{UserA}}}": {"points": 5, "locked": true, "name": "A"},
                     "{{{UserB}}}": {"points": 3, "locked": false, "name": "B"},
                     "{{{guest}}}": {"points": -1, "locked": true, "name": "G"},
                     "{{{between}}}": {"points": 3, "betweenHighPoints": 5, "locked": true, "name": "Bee"}}
                    """),
                Round(R(2), Session, 2, createdHours: 1, sortOrder: 1, active: false, average: 2.5m, selections: $$$"""
                    {"{{{UserA}}}": {"points": 8, "locked": true, "name": "A"}}
                    """),
                Round(R(3), Session, 3, createdHours: 2, sortOrder: 2, active: false, selections: $$$"""
                    {"{{{UserA}}}": {"points": -1, "locked": true, "name": "A"}, "{{{UserB}}}": {"points": -1, "locked": true, "name": "B"}}
                    """),
                Round(R(4), Session, 4, createdHours: 3, sortOrder: 3, active: false, selections: $$$"""
                    {"{{{UserA}}}": {"points": 5, "locked": false, "name": "A"}}
                    """));
        await fixture.SourceSqlAsync($"""
            insert into public.poker_sessions (id, room_id, current_round_number, created_at) values ('{Session}', 'quick1', 1, '{At(0)}');
            {seed}
            """);

        var report = await fixture.RunAsync();

        var rounds = (await fixture.RowsAsync("select id, state, is_active, winner_points, revealed_at, jira_points, jira_points_at, jira_points_by from poker_rounds"))
            .ToDictionary(x => (Guid)x["id"]!);
        Assert.Equal(("revealed", (short?)5), ((string)rounds[R(1)]["state"]!, (short?)rounds[R(1)]["winner_points"]));
        Assert.Equal(T0.AddHours(0.5), ((DateTime)rounds[R(1)]["revealed_at"]!).ToUniversalTime());
        Assert.Equal(("revealed", (short?)8, false), ((string)rounds[R(2)]["state"]!, (short?)rounds[R(2)]["winner_points"], (bool)rounds[R(2)]["is_active"]!));
        Assert.Equal(("revealed", (short?)0), ((string)rounds[R(3)]["state"]!, (short?)rounds[R(3)]["winner_points"]));
        Assert.Equal(("voting", (short?)null), ((string)rounds[R(4)]["state"]!, (short?)rounds[R(4)]["winner_points"]));
        Assert.All(rounds.Values, x => Assert.Null(x["jira_points"] ?? x["jira_points_at"] ?? x["jira_points_by"]));

        Assert.Equal(
            new[] { $"{UserA}:5:locked", $"{UserB}:abstained:locked", $"{SyntheticIds.Guest(guest.ToString())}:abstained:locked",
                    $"{SyntheticIds.Guest(between.ToString())}:3-5:locked" }.Order(StringComparer.Ordinal),
            (await VotesAsync(fixture, R(1))).Order(StringComparer.Ordinal));
        Assert.Empty(await VotesAsync(fixture, R(4)));

        var mismatch = Assert.Single(report.Findings("pokerWinnerMismatches"));
        Assert.Equal(R(2).ToString(), mismatch!["round"]!.GetValue<string>());
        Assert.Equal(2.5m, mismatch["averagePoints"]!.GetValue<decimal>());
        Assert.Equal(8, mismatch["winner"]!.GetValue<short>());
        Assert.Equal(1, report.Counter("poker.winnerMismatches"));
    }

    [Fact]
    public async Task Guest_names_are_scrubbed_and_the_latest_name_wins_and_merged_users_vote_once() {
        var fixture = await CreateAsync(databases);
        var emailGuest = Guid.Parse("9a000000-0000-0000-0000-000000000001");
        var renamed = Guid.Parse("9a000000-0000-0000-0000-000000000002");
        var seed = Rounds(
                Round(R(2), Session, 2, createdHours: 2, sortOrder: 1, selections: $$$"""
                    {"{{{renamed}}}": {"points": 3, "locked": true, "name": "New Name"}}
                    """),
                Round(R(1), Session, 1, createdHours: 1, sortOrder: 0, selections: $$$"""
                    {"{{{emailGuest}}}": {"points": 5, "locked": true, "name": " carol@corp.example.org "},
                     "{{{renamed}}}": {"points": 3, "locked": true, "name": "Old Name"},
                     "{{{MergedLoser}}}": {"points": 8, "locked": true, "name": "Loser"},
                     "{{{MergedKept}}}": {"points": 2, "locked": true, "name": "Kept"}}
                    """));
        await fixture.SourceSqlAsync($"""
            insert into public.poker_sessions (id, room_id, current_round_number, created_at) values ('{Session}', 'quick1', 1, '{At(0)}');
            {seed}
            """);

        var report = await fixture.RunAsync();

        var names = (await fixture.RowsAsync("select id, display_name from principals where kind = 'guest'"))
            .ToDictionary(x => (Guid)x["id"]!, x => (string)x["display_name"]!);
        Assert.Equal(DisplayNames.Scrub("carol@corp.example.org"), names[SyntheticIds.Guest(emailGuest.ToString())]);
        Assert.Matches("^Guest-[0-9a-f]{4}$", names[SyntheticIds.Guest(emailGuest.ToString())]);
        Assert.Equal("New Name", names[SyntheticIds.Guest(renamed.ToString())]);
        Assert.Contains($"{MergedKept}:2:locked", await VotesAsync(fixture, R(1)));
        Assert.DoesNotContain(await VotesAsync(fixture, R(1)), x => x.StartsWith(MergedLoser.ToString(), StringComparison.Ordinal));
        Assert.Equal(1, report.Counter("poker.mergedVoteCollisions"));
        Assert.Equal(0L, await fixture.Harness.TargetScalarAsync<long>("select count(*) from principals where display_name like '%@%'"));
    }

    [Fact]
    public async Task Ticket_titles_and_parent_summaries_reach_neither_the_target_nor_the_archive() {
        var fixture = await CreateAsync(databases);
        var seed = Rounds(
                Round(R(1), Session, 1, createdHours: 0, sortOrder: 0, ticket: " abc-12 ", parent: "abc-1",
                    title: "CANARY title 7731", parentSummary: "CANARY parent 7731", slackTs: "1700000000.0001"),
                Round(R(2), Session, 2, createdHours: 1, sortOrder: 1, ticket: "round:2", title: "CANARY title 7732"),
                Round(R(3), Session, 3, createdHours: 2, sortOrder: 2, ticket: "not a key", parent: "nope"),
                Round(R(4), Session, 4, createdHours: 3, sortOrder: 3, ticket: "42"));
        await fixture.SourceSqlAsync($"""
            insert into public.poker_sessions (id, room_id, current_round_number, created_at) values ('{Session}', 'quick1', 1, '{At(0)}');
            {seed}
            """);
        var runner = fixture.Runner();

        var report = await runner.RunAsync(new RunOptions(), TestContext.Current.CancellationToken);

        var keys = (await fixture.RowsAsync("select id, ticket_key, parent_key from poker_rounds")).ToDictionary(x => (Guid)x["id"]!);
        Assert.Equal(("ABC-12", "ABC-1"), ((string?)keys[R(1)]["ticket_key"], (string?)keys[R(1)]["parent_key"]));
        Assert.All([R(2), R(3), R(4)], x => Assert.Null(keys[x]["ticket_key"] ?? keys[x]["parent_key"]));
        Assert.Equal(2, report.Counter("poker.invalidTicketKeys"));
        Assert.Equal(1, report.Counter("poker.invalidParentKeys"));

        var everything = await fixture.Harness.TargetScalarAsync<string>("""
            select coalesce(string_agg(t::text, ' '), '') from (
              select row_to_json(x)::text as t from poker_rounds x
              union all select row_to_json(x)::text from poker_sessions x
              union all select row_to_json(x)::text from principals x
              union all select row::text from archive.rows
              union all select report::text from archive.import_runs) all_rows
            """);
        Assert.DoesNotContain("CANARY", everything, StringComparison.Ordinal);
        Assert.Contains("1700000000.0001", everything, StringComparison.Ordinal);
        Assert.DoesNotContain(runner.SourceStatements, x => x.Contains("ticket_title", StringComparison.Ordinal) || x.Contains("ticket_parent_summary", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Session_codes_fall_back_to_the_id_and_case_insensitive_collisions_are_kept() {
        var fixture = await CreateAsync(databases);
        Guid nullRoom = S(1), upper = S(2), lower = S(3), first = S(4), duplicate = S(5), invalid = S(6);
        await fixture.SourceSqlAsync($"""
            -- The live database's constraints are unverified (schema.md §0); the importer must not rely on this one.
            alter table public.poker_sessions drop constraint poker_sessions_room_id_key;
            insert into public.poker_sessions (id, room_id, created_at) values
              ('{nullRoom}', null, '{At(0)}'),
              ('{upper}', 'ROOM42', '{At(1)}'),
              ('{lower}', 'room42', '{At(2)}'),
              ('{first}', 'dup-1', '{At(3)}'),
              ('{duplicate}', 'dup-1', '{At(4)}'),
              ('{invalid}', 'has space', '{At(5)}');
            """);

        var report = await fixture.RunAsync();

        var codes = (await fixture.RowsAsync("select id, code, current_round_id, next_round_number from poker_sessions"))
            .ToDictionary(x => (Guid)x["id"]!);
        Assert.Equal(nullRoom.ToString(), codes[nullRoom]["code"]);
        Assert.Equal("ROOM42", codes[upper]["code"]);
        Assert.Equal("room42", codes[lower]["code"]);
        Assert.Equal("dup-1", codes[first]["code"]);
        Assert.Equal(duplicate.ToString(), codes[duplicate]["code"]);
        Assert.Equal(invalid.ToString(), codes[invalid]["code"]);
        Assert.All(codes.Values, x => Assert.Null(x["current_round_id"]));
        Assert.All(codes.Values, x => Assert.Equal(1, x["next_round_number"]));
        Assert.Equal(["duplicate", "invalid"], report.Findings("pokerReplacedCodes").Select(x => x!["reason"]!.GetValue<string>()).ToArray());
        var collision = Assert.Single(report.Findings("pokerCodeCollisions"));
        Assert.Equal("ROOM42", collision!["code"]!.GetValue<string>());
        Assert.Equal(6, report.Table(PokerMap.SessionsTable).Imported);
    }

    [Fact]
    public async Task A_room_id_that_is_another_sessions_id_text_does_not_stop_the_import() {
        var fixture = await CreateAsync(databases);
        Guid squatter = S(1), owner = S(2);
        await fixture.SourceSqlAsync($"""
            alter table public.poker_sessions drop constraint poker_sessions_room_id_key;
            insert into public.poker_sessions (id, room_id, created_at) values
              ('{squatter}', '{owner}', '{At(0)}'),
              ('{owner}', null, '{At(1)}');
            """);

        var report = await fixture.RunAsync();

        var codes = (await fixture.RowsAsync("select id, code from poker_sessions")).ToDictionary(x => (Guid)x["id"]!, x => (string)x["code"]!);
        Assert.Equal(owner.ToString(), codes[squatter]);
        Assert.Equal(owner.ToString("N"), codes[owner]);
        var replaced = Assert.Single(report.Findings("pokerReplacedCodes"));
        Assert.Equal((owner.ToString(), "duplicate"), (replaced!["session"]!.GetValue<string>(), replaced["reason"]!.GetValue<string>()));
        Assert.Equal(2, report.Table(PokerMap.SessionsTable).Imported);
    }

    [Fact]
    public async Task A_null_average_is_reported_and_never_read_as_zero() {
        var fixture = await CreateAsync(databases);
        var seed = Rounds(Round(R(1), Session, 1, createdHours: 0, sortOrder: 0, gameState: "Playing", average: null, selections: $$$"""
            {"{{{UserA}}}": {"points": 5, "locked": true, "name": "A"}}
            """));
        await fixture.SourceSqlAsync($"""
            -- The live database's constraints are unverified (schema.md §0).
            alter table public.poker_session_rounds alter column average_points drop not null;
            insert into public.poker_sessions (id, room_id, current_round_number, created_at) values ('{Session}', 'quick1', 1, '{At(0)}');
            {seed}
            """);

        var report = await fixture.RunAsync();

        Assert.Equal(1, report.Counter("poker.nullAveragePoints"));
        Assert.Equal(R(1).ToString(), Assert.Single(report.Findings("pokerNullAveragePoints"))!.GetValue<string>());
        Assert.Empty(report.Findings("pokerWinnerMismatches"));
        Assert.Equal(0, report.Counter("poker.winnerMismatches"));
        Assert.Equal((short)5, (await fixture.RowAsync("select winner_points from poker_rounds"))["winner_points"]);
    }

    [Fact]
    public async Task A_guest_who_used_poker_and_retro_is_one_principal() {
        var fixture = await CreateAsync(databases);
        var browser = Guid.Parse("9a000000-0000-0000-0000-0000000000ab");
        var seed = Rounds(Round(R(1), Session, 1, createdHours: 0, sortOrder: 0, selections: $$$"""
            {"{{{browser}}}": {"points": 5, "locked": true, "name": "Gwen"}}
            """));
        await fixture.SourceSqlAsync($"""
            insert into public.poker_sessions (id, room_id, current_round_number, created_at) values ('{Session}', 'quick1', 1, '{At(0)}');
            {seed}
            insert into public.retro_boards (id, room_id, title, created_at, updated_at) values ('{S(9)}', 'RETRO1', 'Retro', '{At(0)}', '{At(0)}');
            insert into public.retro_columns (id, board_id, title, color, position) values ('{S(10)}', '{S(9)}', 'Good', 'bg-green-100', 0);
            insert into public.retro_items (id, board_id, column_id, text, author, session_id, created_at, updated_at) values
              ('{S(11)}', '{S(9)}', '{S(10)}', '<p>hi</p>', 'Gwen', '{browser.ToString().ToUpperInvariant()}', '{At(1)}', '{At(1)}');
            """);
        var runner = fixture.Harness.Runner(stages: [new MinimalIdentityStage(), new PokerMap(), new RetroMap(), new ArchiveStage()]);

        await runner.RunAsync(new RunOptions(), TestContext.Current.CancellationToken);

        var guest = SyntheticIds.Guest(browser);
        Assert.Equal(guest, (await fixture.RowAsync("select id from principals where kind = 'guest'"))["id"]);
        Assert.Equal(guest, (await fixture.RowAsync("select principal_id from poker_votes"))["principal_id"]);
        Assert.Equal(guest, (await fixture.RowAsync("select author_id from retro_cards"))["author_id"]);
    }

    private static Guid R(int n) => new($"4d000000-0000-0000-0000-{n:D12}");

    private static Guid S(int n) => new($"5e000000-0000-0000-0000-{n:D12}");

    private static string Rounds(params string[] values) => $"""
        insert into public.poker_session_rounds (id, session_id, round_number, created_at, completed_at, sort_order, game_state, is_active,
                                                 is_pending_round, average_points, selections, ticket_number, ticket_parent_key,
                                                 ticket_title, ticket_parent_summary, slack_message_ts) values
        {string.Join(",\n", values)};
        """;

    private static string Round(
        Guid id,
        Guid session,
        int number,
        double createdHours,
        int sortOrder,
        string gameState = "Selection",
        bool active = true,
        bool pending = false,
        decimal? average = 0,
        double? completedHours = null,
        string selections = "{}",
        string? ticket = null,
        string? parent = null,
        string? title = null,
        string? parentSummary = null,
        string? slackTs = null) =>
        $"('{id}', '{session}', {number}, '{At(createdHours)}', '{At(completedHours ?? createdHours)}', {sortOrder}, '{gameState}', {active}, " +
        $"{pending}, {average?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "null"}, {Literal(selections.Trim())}::jsonb, " +
        $"{Literal(ticket)}, {Literal(parent)}, {Literal(title)}, {Literal(parentSummary)}, {Literal(slackTs)})";

    private static string Literal(string? value) => value is null ? "null" : $"'{value.Replace("'", "''", StringComparison.Ordinal)}'";

    private static async Task<List<string>> VotesAsync(PokerFixture fixture, Guid round) =>
        [.. (await fixture.RowsAsync($"select principal_id, points, high_points, locked, abstained from poker_votes where round_id = '{round}'"))
            .Select(x => $"{x["principal_id"]}:{((bool)x["abstained"]! ? "abstained" : x["high_points"] is null ? x["points"] : $"{x["points"]}-{x["high_points"]}")}:{((bool)x["locked"]! ? "locked" : "open")}")];
}
