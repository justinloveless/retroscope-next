using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using Retroscope.Integration.Tests.Actors;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Realtime.Actors;
using Retroscope.Server.Realtime.Rooms;
using static Retroscope.Integration.Tests.Actors.ActorHarness;
using static Retroscope.Integration.Tests.RetroRooms.RetroScope;

namespace Retroscope.Integration.Tests.RetroRooms;

[Trait("Category", "RetroRoom")]
[Collection(SharedPostgres.Name)]
public sealed class RetroPersistenceTests(PostgresFixture postgres) {
    private static readonly CardId First = new(Id(0x600));
    private static readonly CardId Second = new(Id(0x601));
    private static readonly CommentId Comment = new(Id(0x700));

    [Fact]
    public async Task Anonymous_posts_store_no_principal_in_any_column_and_their_author_still_owns_them() {
        await using var scope = await RetroScope.StartAsync(postgres);
        var registry = scope.NewRegistry();
        var alice = await JoinAsync(registry, TeamBoardRoom, Alice);

        await AckAsync(registry, TeamBoardRoom, alice, 1, new RetroCardAdd(First, Actions, $"Ship it {Image(Image1)}", Anonymous: true));
        await AckAsync(registry, TeamBoardRoom, alice, 2, new RetroCommentAdd(Comment, First, $"Me too {Image(Image2)}", Anonymous: true));

        var card = Assert.Single(await scope.RowsAsync("select author_id, legacy_author, octet_length(anon_tag) from retro_cards"));
        var comment = Assert.Single(await scope.RowsAsync("select author_id, legacy_author, octet_length(anon_tag) from retro_comments"));
        Assert.Equal([null, null, 32], card);
        Assert.Equal([null, null, 32], comment);
        Assert.Equal(
            [[Image1.Value, null], [Image2.Value, null]],
            await scope.RowsAsync("select id, created_by from blobs where purpose = 'board_image' and attached_at is not null order by id"));
        Assert.Equal([[null, First.Value]], await scope.RowsAsync("select created_by, source_card_id from team_action_items"));
        Assert.Equal(0L, await scope.ScalarAsync<long>(
            """
            select (select count(*) from retro_cards where author_id = $1) + (select count(*) from retro_comments where author_id = $1)
                 + (select count(*) from blobs where purpose = 'board_image' and attached_at is not null and created_by = $1)
                 + (select count(*) from team_action_items where created_by = $1)
            """,
            Alice.Who.Id.Value));

        await registry.ShutdownAsync(Token);
        var restarted = scope.NewRegistry();
        var again = await JoinAsync(restarted, TeamBoardRoom, Alice);
        var bob = await JoinAsync(restarted, TeamBoardRoom, Bob);

        Assert.Equal(NackCode.Forbidden, (await NackAsync(restarted, TeamBoardRoom, bob, 1, new RetroCardEdit(First, "Mine now", 1))).Code);
        await AckAsync(restarted, TeamBoardRoom, again, 3, new RetroCardEdit(First, "Ship it today", 1));
        await AckAsync(restarted, TeamBoardRoom, again, 4, new RetroCommentDelete(Comment));
        Assert.Equal("Ship it today", await scope.ScalarAsync<string>("select body from retro_cards where id = $1", First.Value));
        Assert.Null(await scope.ScalarAsync<Guid?>("select author_id from retro_cards where id = $1", First.Value));
    }

    [Fact]
    public async Task Referenced_images_are_attached_and_a_foreign_one_refuses_the_whole_command() {
        await using var scope = await RetroScope.StartAsync(postgres);
        var registry = scope.NewRegistry();
        var alice = await JoinAsync(registry, TeamBoardRoom, Alice);

        await AckAsync(registry, TeamBoardRoom, alice, 1, new RetroCardAdd(First, Good, $"Look {Image(Image1)}", Anonymous: false));
        var fromQuickBoard = await NackAsync(registry, TeamBoardRoom, alice, 2, new RetroCardAdd(Second, Good, Image(QuickImage), Anonymous: false));
        var avatar = await NackAsync(registry, TeamBoardRoom, alice, 3, new RetroCardAdd(Second, Good, $"{Image(Image2)} {Image(Avatar)}", Anonymous: false));
        var missing = await NackAsync(registry, TeamBoardRoom, alice, 4, new RetroCommentAdd(Comment, First, Image(new BlobId(Id(0x5ff))), Anonymous: false));

        Assert.Equal(
            [[Image1.Value, Alice.Who.Id.Value, true], [Image2.Value, Alice.Who.Id.Value, false]],
            await scope.RowsAsync("select id, created_by, attached_at is not null from blobs where board_id = $1 order by id", TeamBoard.Value));
        Assert.All(new[] { fromQuickBoard, avatar, missing }, nack => Assert.Equal(NackCode.Internal, nack.Code));
        Assert.Equal(1L, await scope.ScalarAsync<long>("select count(*) from retro_cards"));
        Assert.Equal(0L, await scope.ScalarAsync<long>("select count(*) from retro_comments"));
        Assert.False(await scope.ScalarAsync<bool>("select attached_at is not null from blobs where id = $1", QuickImage.Value));
    }

    [Fact]
    public async Task Mentions_of_team_members_are_kept_in_step_with_card_and_comment_bodies() {
        await using var scope = await RetroScope.StartAsync(postgres);
        var registry = scope.NewRegistry();
        var alice = await JoinAsync(registry, TeamBoardRoom, Alice);
        var quick = await JoinAsync(registry, QuickBoardRoom, Alice);

        await AckAsync(registry, TeamBoardRoom, alice, 1, new RetroCardAdd(First, Good, $"{Mention(Bob)} and {Mention(Carol)}", Anonymous: false));
        var added = await scope.RowsAsync("select source_kind, source_id, board_id, user_id from mentions");
        await AckAsync(registry, TeamBoardRoom, alice, 2, new RetroCommentAdd(Comment, First, $"cc {Mention(Alice)}", Anonymous: false));
        await AckAsync(registry, TeamBoardRoom, alice, 3, new RetroCardEdit(First, $"Over to {Mention(Alice)}", 1));
        var edited = await scope.RowsAsync("select source_kind, user_id from mentions order by source_kind, user_id");
        await AckAsync(registry, QuickBoardRoom, quick, 1, new RetroCardAdd(Second, QuickColumn, Mention(Bob), Anonymous: false));
        var withQuick = await scope.ScalarAsync<long>("select count(*) from mentions");
        await AckAsync(registry, TeamBoardRoom, alice, 4, new RetroCardDelete(First));

        Assert.Equal([["card", First.Value, TeamBoard.Value, Bob.Who.Id.Value]], added);
        Assert.Equal(
            [["card", Alice.Who.Id.Value], ["comment", Alice.Who.Id.Value]],
            edited);
        Assert.Equal(2L, withQuick);
        Assert.Equal(0L, await scope.ScalarAsync<long>("select count(*) from mentions"));
    }

    [Fact]
    public async Task Stale_preconditions_are_refused_with_the_current_value_and_write_nothing() {
        await using var scope = await RetroScope.StartAsync(postgres);
        var registry = scope.NewRegistry();
        var alice = await JoinAsync(registry, TeamBoardRoom, Alice);
        await AckAsync(registry, TeamBoardRoom, alice, 1, new RetroCardAdd(First, Good, "One", Anonymous: false));
        await AckAsync(registry, TeamBoardRoom, alice, 2, new RetroCardEdit(First, "Two", 1));

        var card = await NackAsync(registry, TeamBoardRoom, alice, 3, new RetroCardEdit(First, "Three", 1));
        var column = await NackAsync(registry, TeamBoardRoom, alice, 4, new RetroColumnUpdate(Good, 7, Title: "Great"));
        var rename = await NackAsync(registry, TeamBoardRoom, alice, 5, new RetroBoardRename("Renamed", "Not the title"));
        var config = await NackAsync(registry, TeamBoardRoom, alice, 6, new RetroConfigUpdate(
            new RetroConfigPatch(AllowAnonymous: false), new RetroConfigPatch(AllowAnonymous: false)));
        var stage = await NackAsync(registry, TeamBoardRoom, alice, 7, new RetroStageSet(RetroStage.Voting, RetroStage.Discussing));
        var timer = await NackAsync(registry, TeamBoardRoom, alice, 8, new RetroTimerPause(TimerState.Running));
        var focus = await NackAsync(registry, TeamBoardRoom, alice, 9, new RetroFocusSet(First, First));

        Assert.All(new[] { card, column, rename, config, stage, timer, focus }, nack => Assert.Equal(NackCode.Stale, nack.Code));
        Assert.Equal("""{"body":"Two","version":2}""", card.D!.Value.GetProperty("current").GetRawText());
        Assert.Equal(
            [["Two", 2, "Good", 1, "Sprint 1", true, "thinking", "idle", null]],
            await scope.RowsAsync(
                """
                select k.body, k.version, c.title, c.version, b.title, b.allow_anonymous, b.stage, b.timer_state, b.focus_card_id
                from retro_cards k join retro_columns c on c.id = $2 join retro_boards b on b.id = k.board_id
                where k.id = $1
                """,
                First.Value,
                Good.Value));
    }

    [Fact]
    public async Task A_running_timer_that_ended_while_down_expires_exactly_once() {
        await using var scope = await RetroScope.StartAsync(postgres);
        var registry = scope.NewRegistry();
        var alice = await JoinAsync(registry, TeamBoardRoom, Alice);
        await AckAsync(registry, TeamBoardRoom, alice, 1, new RetroTimerStart(60_000, Alarm: true, Music: false, TimerState.Idle));
        var endsAt = scope.Time.GetUtcNow().AddMinutes(1).ToUnixTimeMilliseconds();
        await registry.ShutdownAsync(Token);
        scope.Time.Advance(TimeSpan.FromMinutes(5));

        var restarted = scope.NewRegistry();
        var bob = await JoinAsync(restarted, TeamBoardRoom, Bob);
        scope.Time.Advance(TimeSpan.FromMilliseconds(1));
        var expired = await TimerAsync(bob);
        await SettleAsync(restarted, TeamBoardRoom);
        await restarted.ShutdownAsync(Token);
        var third = scope.NewRegistry();
        var bobAgain = await JoinAsync(third, TeamBoardRoom, Bob);
        scope.Time.Advance(TimeSpan.FromMinutes(1));
        await SettleAsync(third, TeamBoardRoom);

        Assert.Equal(new RetroTimer(TimerState.Idle, 60_000, true, false, endsAt, Expired: true), expired);
        Assert.Equal("idle", await scope.ScalarAsync<string>("select timer_state from retro_boards where id = $1", TeamBoard.Value));
        Assert.DoesNotContain(await bobAgain.DrainAsync(), frame => frame is TxFrame tx && tx.Ev.OfType<RetroTimer>().Any());
    }

    [Fact]
    public async Task A_timer_whose_first_fire_lands_early_is_armed_again_and_expires_exactly_once() {
        await using var scope = await RetroScope.StartAsync(postgres);
        var registry = scope.NewRegistry(new EarlyTimers(scope.Time));
        var alice = await JoinAsync(registry, TeamBoardRoom, Alice);
        await AckAsync(registry, TeamBoardRoom, alice, 1, new RetroTimerStart(60_000, Alarm: true, Music: false, TimerState.Idle));
        await SettleAsync(registry, TeamBoardRoom);
        await alice.DrainAsync();

        scope.Time.Advance(TimeSpan.FromMilliseconds(59_950));
        await SettleAsync(registry, TeamBoardRoom);
        var early = await TimerEventsAsync(alice);
        scope.Time.Advance(TimeSpan.FromMilliseconds(50));
        await SettleAsync(registry, TeamBoardRoom);
        var due = await TimerEventsAsync(alice);
        scope.Time.Advance(TimeSpan.FromMinutes(5));
        await SettleAsync(registry, TeamBoardRoom);

        Assert.Empty(early);
        Assert.True(Assert.Single(due).Expired);
        Assert.Empty(await TimerEventsAsync(alice));
        Assert.Equal("idle", await scope.ScalarAsync<string>("select timer_state from retro_boards where id = $1", TeamBoard.Value));
    }

    [Fact]
    public async Task A_timer_started_before_a_restart_expires_exactly_once_after_the_load() {
        await using var scope = await RetroScope.StartAsync(postgres);
        var registry = scope.NewRegistry(new EarlyTimers(scope.Time));
        var alice = await JoinAsync(registry, TeamBoardRoom, Alice);
        await AckAsync(registry, TeamBoardRoom, alice, 1, new RetroTimerStart(60_000, Alarm: true, Music: false, TimerState.Idle));
        scope.Time.Advance(TimeSpan.FromSeconds(10));
        await registry.ShutdownAsync(Token);

        var restarted = scope.NewRegistry(new EarlyTimers(scope.Time));
        var bob = await JoinAsync(restarted, TeamBoardRoom, Bob);
        await SettleAsync(restarted, TeamBoardRoom);
        scope.Time.Advance(TimeSpan.FromMilliseconds(49_990));
        await SettleAsync(restarted, TeamBoardRoom);
        var early = await TimerEventsAsync(bob);
        scope.Time.Advance(TimeSpan.FromMilliseconds(10));
        await SettleAsync(restarted, TeamBoardRoom);
        var due = await TimerEventsAsync(bob);
        scope.Time.Advance(TimeSpan.FromMinutes(5));
        await SettleAsync(restarted, TeamBoardRoom);

        Assert.Empty(early);
        Assert.True(Assert.Single(due).Expired);
        Assert.Empty(await TimerEventsAsync(bob));
        Assert.Equal("idle", await scope.ScalarAsync<string>("select timer_state from retro_boards where id = $1", TeamBoard.Value));
    }

    [Fact]
    public async Task A_card_in_the_action_items_column_creates_the_team_item_in_place_and_keeps_the_team_informed() {
        await using var scope = await RetroScope.StartAsync(postgres);
        var registry = scope.NewRegistry();
        var bob = await JoinAsync(registry, TeamBoardRoom, Bob);
        var alice = await JoinAsync(registry, TeamBoardRoom, Alice);

        await AckAsync(registry, TeamBoardRoom, bob, 1, new RetroCardAdd(First, Actions, "Fix CI", Anonymous: false));
        var item = Assert.Single(await scope.RowsAsync(
            "select id, team_id, text, source_board_id, source_card_id, source_version, created_by from team_action_items"));
        var itemId = (Guid)item[0]!;
        await AckAsync(registry, TeamBoardRoom, bob, 2, new RetroCardEdit(First, "Fix CI today", 1));
        await AckAsync(registry, TeamBoardRoom, alice, 1, new RetroCommentAdd(Comment, First, "On it", Anonymous: false));
        await AckAsync(registry, TeamBoardRoom, alice, 2, new RetroBoardRename("Renamed", "Sprint 1"));
        await AckAsync(registry, TeamBoardRoom, alice, 3, new RetroCardDelete(First));

        Assert.Equal([itemId, Team.Value, "Fix CI", TeamBoard.Value, First.Value, 1, Bob.Who.Id.Value], item);
        Assert.Equal(
            [
                $"created {itemId} {First.Value}",
                $"text {First.Value} 2 Fix CI today",
                $"comments {itemId} 1",
                $"boards {Team.Value} {TeamBoard.Value}",
                $"deleted {First.Value}",
            ],
            scope.Notices.Log);
        Assert.Equal([["Fix CI", null]], await scope.RowsAsync("select text, source_card_id from team_action_items"));
    }

    [Fact]
    public async Task Action_item_comments_from_the_team_page_reach_the_source_card_for_members_only() {
        await using var scope = await RetroScope.StartAsync(postgres);
        var registry = scope.NewRegistry();
        var bob = await JoinAsync(registry, TeamBoardRoom, Bob);
        await AckAsync(registry, TeamBoardRoom, bob, 1, new RetroCardAdd(First, Actions, "Fix CI", Anonymous: false));
        var item = new ActionItemId(await scope.ScalarAsync<Guid>("select id from team_action_items"));

        var accepted = await RetroRoom.AddActionItemCommentAsync(registry, scope.DataSource, Team, item, Comment, "Done?", Alice, Token);
        var otherTeam = await RetroRoom.AddActionItemCommentAsync(
            registry, scope.DataSource, new TeamId(Id(0x101)), item, new CommentId(Id(0x701)), "Hm", Alice, Token);
        var outsider = await RetroRoom.AddActionItemCommentAsync(
            registry, scope.DataSource, Team, item, new CommentId(Id(0x702)), "Hm", Carol, Token);
        var broadcast = (await bob.UntilAsync<TxFrame>()).Ev.OfType<RetroCommentAdded>().Single();

        Assert.Equal(AskStatus.Accepted, accepted.Status);
        Assert.Equal(AskStatus.NotFound, otherTeam.Status);
        Assert.Equal((AskStatus.Rejected, (NackCode?)NackCode.Forbidden), (outsider.Status, outsider.Code));
        Assert.Equal(Comment, broadcast.Comment.Id);
        Assert.Equal([[Comment.Value, First.Value, Alice.Who.Id.Value]], await scope.RowsAsync("select id, card_id, author_id from retro_comments"));
        Assert.Contains($"comments {item.Value} 1", scope.Notices.Log);
    }

    private static async Task<List<RetroTimer>> TimerEventsAsync(TestPeer peer) {
        return (await peer.DrainAsync()).OfType<TxFrame>().SelectMany(tx => tx.Ev.OfType<RetroTimer>()).ToList();
    }

    private static async Task<RetroTimer> TimerAsync(TestPeer peer) {
        while (true) {
            if (await peer.NextAsync() is TxFrame tx && tx.Ev.OfType<RetroTimer>().SingleOrDefault() is { } timer) {
                return timer;
            }
        }
    }
}
