using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Realtime.Actors;
using static Retroscope.Integration.Tests.Actors.ActorHarness;
using static Retroscope.Integration.Tests.RetroRooms.RetroScope;

namespace Retroscope.Integration.Tests.RetroRooms;

/// <summary>Every retro command commits through the real store, and a restarted process loads the same board.</summary>
[Trait("Category", "RetroRoom")]
[Collection(SharedPostgres.Name)]
public sealed class RetroRoundTripTests(PostgresFixture postgres) {
    private static readonly ColumnId Ideas = new(Id(0x310));
    private static readonly ColumnId Doomed = new(Id(0x311));
    private static readonly CardId Named = new(Id(0x600));
    private static readonly CardId Hidden = new(Id(0x601));
    private static readonly CardId Action = new(Id(0x602));
    private static readonly CardId FromGuest = new(Id(0x603));
    private static readonly CardId Gone = new(Id(0x604));
    private static readonly CardId InDoomed = new(Id(0x605));
    private static readonly CommentId Reply = new(Id(0x700));
    private static readonly CommentId Whisper = new(Id(0x701));
    private static readonly CommentId Retracted = new(Id(0x702));
    private static readonly CommentId OnGone = new(Id(0x703));
    private static readonly EndorsementId Thanks = new(Id(0x800));
    private static readonly EndorsementId Withdrawn = new(Id(0x801));

    [Fact]
    public async Task Every_command_survives_a_restart_unchanged() {
        await using var scope = await RetroScope.StartAsync(postgres);
        // Guests no longer take part (UX18), but an imported guest's card is history and reloads with its author.
        await scope.ExecuteAsync(
            "insert into retro_cards (id, board_id, column_id, body, author_id) values ($1, $2, $3, 'Hello from a guest', $4)",
            FromGuest.Value, TeamBoard.Value, Good.Value, Gina.Who.Id.Value);
        var registry = scope.NewRegistry();
        var alice = await JoinAsync(registry, TeamBoardRoom, Alice);
        var bob = await JoinAsync(registry, TeamBoardRoom, Bob);
        var q = 0;

        await AckAsync(registry, TeamBoardRoom, alice, ++q, new RetroColumnAdd(Ideas, "Ideas", ColumnColor.Purple));
        await AckAsync(registry, TeamBoardRoom, alice, ++q, new RetroColumnUpdate(Ideas, 1, Title: "Ideas!", Color: ColumnColor.Pink));
        await AckAsync(registry, TeamBoardRoom, alice, ++q, new RetroColumnReorder([Ideas, Actions, Good]));
        await AckAsync(registry, TeamBoardRoom, alice, ++q, new RetroColumnSetActionItems(Ideas));
        await AckAsync(registry, TeamBoardRoom, alice, ++q, new RetroCardAdd(Named, Good, $"Hi {Mention(Bob)} {Image(Image1)}", Anonymous: false));
        await AckAsync(registry, TeamBoardRoom, alice, ++q, new RetroCardAdd(Hidden, Good, "Psst", Anonymous: true));
        await AckAsync(registry, TeamBoardRoom, bob, 1, new RetroCardAdd(Action, Ideas, "Fix the build", Anonymous: false));
        await AckAsync(registry, TeamBoardRoom, alice, ++q, new RetroCardEdit(Named, $"Hello {Mention(Bob)}", 1));
        await AckAsync(registry, TeamBoardRoom, bob, 2, new RetroVoteSet(Hidden, On: true));
        await AckAsync(registry, TeamBoardRoom, alice, ++q, new RetroVoteSet(FromGuest, On: true));
        await AckAsync(registry, TeamBoardRoom, alice, ++q, new RetroVoteSet(Named, On: true));
        await AckAsync(registry, TeamBoardRoom, alice, ++q, new RetroVoteSet(Named, On: false));
        await AckAsync(registry, TeamBoardRoom, bob, 3, new RetroCommentAdd(Reply, Named, "Agreed", Anonymous: false));
        await AckAsync(registry, TeamBoardRoom, alice, ++q, new RetroCommentAdd(Whisper, Named, $"Quietly {Image(Image2)}", Anonymous: true));
        await AckAsync(registry, TeamBoardRoom, bob, 4, new RetroCommentAdd(Retracted, Named, "Oops", Anonymous: false));
        await AckAsync(registry, TeamBoardRoom, bob, 5, new RetroCommentDelete(Retracted));
        await AckAsync(registry, TeamBoardRoom, alice, ++q, new RetroCardAdd(Gone, Good, "Temporary", Anonymous: false));
        await AckAsync(registry, TeamBoardRoom, bob, 6, new RetroCommentAdd(OnGone, Gone, $"About to go {Mention(Alice)}", Anonymous: false));
        await AckAsync(registry, TeamBoardRoom, alice, ++q, new RetroCardDelete(Gone));
        await AckAsync(registry, TeamBoardRoom, alice, ++q, new RetroColumnAdd(Doomed, "Doomed", ColumnColor.Red));
        await AckAsync(registry, TeamBoardRoom, alice, ++q, new RetroCardAdd(InDoomed, Doomed, "Bye", Anonymous: false));
        await AckAsync(registry, TeamBoardRoom, alice, ++q, new RetroColumnDelete(Doomed));
        await AckAsync(registry, TeamBoardRoom, alice, ++q, new RetroBoardRename("Sprint 1 retro", "Sprint 1"));
        await AckAsync(registry, TeamBoardRoom, alice, ++q, new RetroConfigUpdate(
            new RetroConfigPatch(MaxVotes: 5, VoteEmoji: "🔥", SortChronologically: true, AllowSelfVotes: false),
            new RetroConfigPatch(MaxVotes: 0, VoteEmoji: "👍", SortChronologically: false, AllowSelfVotes: true)));
        await AckAsync(registry, TeamBoardRoom, alice, ++q, new RetroStageSet(RetroStage.Thinking, RetroStage.Voting));
        await AckAsync(registry, TeamBoardRoom, alice, ++q, new RetroReadySet(RetroStage.Voting, Ready: true));
        await AckAsync(registry, TeamBoardRoom, bob, 7, new RetroReadySet(RetroStage.Voting, Ready: true));
        await AckAsync(registry, TeamBoardRoom, bob, 8, new RetroReadySet(RetroStage.Voting, Ready: false));
        await AckAsync(registry, TeamBoardRoom, alice, ++q, new RetroFocusSet(Named, null));
        await AckAsync(registry, TeamBoardRoom, alice, ++q, new RetroTimerStart(60_000, Alarm: true, Music: true, TimerState.Idle));
        scope.Time.Advance(TimeSpan.FromSeconds(10));
        await AckAsync(registry, TeamBoardRoom, alice, ++q, new RetroTimerPause(TimerState.Running));
        await AckAsync(registry, TeamBoardRoom, alice, ++q, new RetroTimerSetFlags(Alarm: false));
        await AckAsync(registry, TeamBoardRoom, alice, ++q, new RetroEndorseGive(Thanks, Bob.Who.Id, Kudos));
        await AckAsync(registry, TeamBoardRoom, bob, 9, new RetroEndorseGive(Withdrawn, Alice.Who.Id, Kudos));
        await AckAsync(registry, TeamBoardRoom, bob, 10, new RetroEndorseRevoke(Withdrawn));
        var archived = await registry.AskAsync<RetroState, RetroInput>(TeamBoardRoom, new RetroInput.ArchiveSet(true, Alice.Who.Id), null, Token);
        Assert.Equal(AskStatus.Accepted, archived.Status);
        var live = await scope.DescribeAsync(registry, TeamBoardRoom, Alice, Bob, Gina);
        await registry.ShutdownAsync(Token);

        var restarted = scope.NewRegistry();
        var loaded = await scope.DescribeAsync(restarted, TeamBoardRoom, Alice, Bob, Gina);

        Assert.Equal(live, loaded);
        Assert.Contains("\"stage\":\"voting\"", loaded, StringComparison.Ordinal);
        Assert.Contains("\"remainingMs\":50000", loaded, StringComparison.Ordinal);
        Assert.Equal(3L, await scope.ScalarAsync<long>("select count(*) from retro_columns where board_id = $1", TeamBoard.Value));
        Assert.Equal(Ideas.Value, await scope.ScalarAsync<Guid>("select id from retro_columns where board_id = $1 and is_action_items", TeamBoard.Value));
        Assert.Equal(Alice.Who.Id.Value, await scope.ScalarAsync<Guid>("select archived_by from retro_boards where id = $1", TeamBoard.Value));
    }

    [Fact]
    public async Task Unarchiving_and_a_timer_that_runs_reload_as_they_were() {
        await using var scope = await RetroScope.StartAsync(postgres);
        var registry = scope.NewRegistry();
        var alice = await JoinAsync(registry, OtherBoardRoom, Alice);
        await AckAsync(registry, OtherBoardRoom, alice, 1, new RetroTimerStart(120_000, Alarm: true, Music: false, TimerState.Idle));
        await registry.AskAsync<RetroState, RetroInput>(OtherBoardRoom, new RetroInput.ArchiveSet(true, Alice.Who.Id), null, Token);
        await registry.AskAsync<RetroState, RetroInput>(OtherBoardRoom, new RetroInput.ArchiveSet(false, Alice.Who.Id), null, Token);
        var live = await scope.DescribeAsync(registry, OtherBoardRoom, Alice);
        await registry.ShutdownAsync(Token);

        var loaded = await scope.DescribeAsync(scope.NewRegistry(), OtherBoardRoom, Alice);

        Assert.Equal(live, loaded);
        Assert.Contains("\"state\":\"running\"", loaded, StringComparison.Ordinal);
        Assert.Null(await scope.ScalarAsync<Guid?>("select archived_by from retro_boards where id = $1", OtherBoard.Value));
    }
}
