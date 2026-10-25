using System.Text;
using System.Text.Json.Nodes;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using Retroscope.Core.Tests.Scaffold;
using static Retroscope.Core.Tests.Retro.RetroFixture;

namespace Retroscope.Core.Tests.Retro;

/// <summary>
/// Emits <c>tests/golden/transcripts/retro-*.json</c> (DESIGN §g.4): the frames one viewer receives, starting
/// with its snapshot, and the snapshot a fresh subscription would get at the end. <c>web/src/retro/model.js</c>
/// must fold the frames into that state. A transcript that differs from the committed file is rewritten and the
/// test fails, so a second run passes and the diff shows up for review.
/// </summary>
[Trait("Category", "Retro")]
public sealed class RetroTranscriptTests {
    [Fact]
    public void Board() {
        var driver = new RetroRoomDriver(new RetroFixture());
        var fx = driver.Fixture;
        var alice = driver.Watch(Alice);
        var dave = driver.Watch(Dave);

        var first = fx.NewCardId();
        driver.Accept(Alice, new RetroCardAdd(first, Good, "Pairing on **CI** helped", false));
        var secret = fx.NewCardId();
        driver.Accept(Bob, new RetroCardAdd(secret, Bad, "Standups run long", true));
        var demo = fx.NewCardId();
        driver.Accept(Dave, new RetroCardAdd(demo, Good, "Nice demo", false));
        driver.Accept(Alice, new RetroCardEdit(first, "Pairing on **CI** helped a lot", 1));
        driver.Accept(Bob, new RetroVoteSet(first, true));
        driver.Accept(Dave, new RetroVoteSet(first, true));
        driver.Accept(Dave, new RetroVoteSet(secret, true));
        var comment = fx.NewCommentId();
        driver.Accept(Alice, new RetroCommentAdd(comment, secret, "Agreed", false));
        driver.Accept(Dave, new RetroCommentAdd(fx.NewCommentId(), secret, "+1", false));
        var ideas = fx.NewColumnId();
        driver.Accept(Alice, new RetroColumnAdd(ideas, "Ideas", ColumnColor.Purple));
        driver.Accept(Bob, new RetroColumnUpdate(ideas, 1, Title: "Experiments"));
        driver.Accept(Alice, new RetroColumnReorder([ideas, Good, Bad, Actions]));
        driver.Accept(Alice, new RetroCardAdd(fx.NewCardId(), ideas, "Try mob programming", false));
        driver.Accept(Bob, new RetroFocusSet(secret, null));
        driver.Accept(Bob, new RetroCardDelete(secret));
        driver.Accept(Alice, new RetroColumnSetActionItems(ideas));
        driver.Accept(Alice, new RetroColumnDelete(Actions));
        driver.Accept(Alice, new RetroBoardRename("Sprint 42 retro (done)", "Sprint 42 retro"));
        driver.Accept(Alice, new RetroConfigUpdate(new RetroConfigPatch(ShowAuthorNames: false, MaxVotes: 3), new RetroConfigPatch(ShowAuthorNames: true, MaxVotes: 0)));
        driver.Accept(Dave, new RetroVoteSet(first, false));

        Emit("retro-board-alice", driver, alice);
        Emit("retro-board-dave", driver, dave);
    }

    [Fact]
    public void Stages_and_readiness() {
        var driver = new RetroRoomDriver(new RetroFixture());
        var fx = driver.Fixture;
        var bob = driver.Watch(Bob);

        driver.Accept(Alice, new RetroConfigUpdate(
            new RetroConfigPatch(StagesEnabled: true, EnforceReadiness: true, AllowSelfVotes: false),
            new RetroConfigPatch(StagesEnabled: false, EnforceReadiness: false, AllowSelfVotes: true)));
        var card = fx.NewCardId();
        driver.Accept(Alice, new RetroCardAdd(card, Good, "Fewer meetings", false));
        var anonymous = fx.NewCardId();
        driver.Accept(Bob, new RetroCardAdd(anonymous, Bad, "Flaky tests", true));
        foreach (var who in new[] { Alice, Bob, Dave }) {
            driver.Accept(who, new RetroReadySet(RetroStage.Thinking, true));
        }
        driver.Accept(Alice, new RetroStageSet(RetroStage.Thinking, RetroStage.Voting));
        driver.Accept(Bob, new RetroVoteSet(card, true));
        driver.Accept(Dave, new RetroVoteSet(card, true));
        driver.Accept(Dave, new RetroVoteSet(anonymous, true));
        driver.Accept(Bob, new RetroReadySet(RetroStage.Voting, true));
        driver.Accept(Alice, new RetroReadySet(RetroStage.Voting, true));
        driver.Accept(Dave, new RetroReadySet(RetroStage.Voting, true));
        driver.Accept(Alice, new RetroStageSet(RetroStage.Voting, RetroStage.Discussing));
        driver.Accept(Dave, new RetroFocusSet(card, null));
        driver.Accept(Bob, new RetroCardAdd(fx.NewCardId(), Actions, "Quarantine flaky tests", false));
        driver.Accept(Alice, new RetroStageSet(RetroStage.Discussing, RetroStage.Thinking));
        driver.Accept(Alice, new RetroConfigUpdate(new RetroConfigPatch(EnforceReadiness: false), new RetroConfigPatch(EnforceReadiness: true)));
        driver.Accept(Alice, new RetroStageSet(RetroStage.Thinking, RetroStage.Closed));
        driver.Accept(null, new RetroInput.ArchiveSet(true, Alice.Id));

        Emit("retro-stages-bob", driver, bob);
    }

    [Fact]
    public void Team_board_roster_and_endorsements() {
        var driver = new RetroRoomDriver(new RetroFixture());
        var fx = driver.Fixture;
        var bob = driver.Watch(Bob);

        var action = fx.NewCardId();
        driver.Accept(Alice, new RetroCardAdd(action, Actions, $"Pair with [[mention:{Bob.Id}:Bob]] on deploys", false));
        driver.Accept(Dave, new RetroCardAdd(fx.NewCardId(), Good, "Thanks for having me", false));
        driver.Accept(Alice, new RetroCardAdd(fx.NewCardId(), Actions, "Write the runbook", true));
        driver.Accept(Alice, new RetroCardEdit(action, "Pair with Bob on deploys", 1));
        driver.Accept(Alice, new RetroEndorseGive(fx.NewEndorsementId(), Bob.Id, Helper));
        var kudos = fx.NewEndorsementId();
        driver.Accept(Bob, new RetroEndorseGive(kudos, Alice.Id, Helper));
        driver.Accept(Bob, new RetroEndorseRevoke(kudos));
        driver.Accept(null, new RetroInput.TeamSettingsChanged(5));
        driver.Accept(Bob, new RetroTimerStart(600_000, true, false, TimerState.Idle));
        driver.Accept(null, new RetroInput.MembersChanged([.. fx.State.Members.Where(m => m.Pid != Dave.Id)]));

        Emit("retro-team-member", driver, bob);
    }

    [Fact]
    public void Timer() {
        var driver = new RetroRoomDriver(new RetroFixture());
        var fx = driver.Fixture;
        var dave = driver.Watch(Dave);

        driver.Accept(Alice, new RetroTimerStart(300_000, true, true, TimerState.Idle));
        fx.Now += TimeSpan.FromMinutes(1);
        driver.Accept(Bob, new RetroTimerPause(TimerState.Running));
        driver.Accept(Bob, new RetroTimerSetFlags(Music: false));
        driver.Accept(Alice, new RetroTimerResume(TimerState.Paused));
        var due = fx.Decider.Timers(fx.State, fx.Presence, fx.Now).Single();
        fx.Now = due.DueAt;
        driver.Accept(null, due.Command);
        driver.Accept(Alice, new RetroTimerStart(60_000, false, false, TimerState.Idle));
        driver.Accept(Alice, new RetroTimerReset(TimerState.Running));

        Emit("retro-timer-dave", driver, dave);
    }

    [Fact]
    public void Posts_in_the_same_millisecond() {
        var driver = new RetroRoomDriver(new RetroFixture());
        var fx = driver.Fixture;
        var alice = driver.Watch(Alice);
        var higher = new CardId(Guid.Parse("ca000000-0000-4000-8000-0000000000f2"));
        var lower = new CardId(Guid.Parse("ca000000-0000-4000-8000-0000000000f1"));

        // Both posts share a wire millisecond, but the clock orders them against their ids.
        fx.Now = Start.AddTicks(2_000);
        driver.Accept(Alice, new RetroCardAdd(higher, Good, "Posted first", false));
        fx.Now = Start.AddTicks(7_000);
        driver.Accept(Bob, new RetroCardAdd(lower, Good, "Posted second", false));
        fx.Now = Start.AddSeconds(1).AddTicks(2_000);
        driver.Accept(Alice, new RetroCommentAdd(new CommentId(Guid.Parse("c0000000-0000-4000-8000-0000000000f2")), lower, "First", false));
        fx.Now = Start.AddSeconds(1).AddTicks(7_000);
        driver.Accept(Bob, new RetroCommentAdd(new CommentId(Guid.Parse("c0000000-0000-4000-8000-0000000000f1")), lower, "Second", false));
        fx.Now = Start.AddSeconds(2).AddTicks(2_000);
        driver.Accept(Alice, new RetroEndorseGive(new EndorsementId(Guid.Parse("e0000000-0000-4000-8000-0000000000f2")), Bob.Id, Helper));
        fx.Now = Start.AddSeconds(2).AddTicks(7_000);
        driver.Accept(Bob, new RetroEndorseGive(new EndorsementId(Guid.Parse("e0000000-0000-4000-8000-0000000000f1")), Alice.Id, Helper));

        Emit("retro-same-millisecond-alice", driver, alice);
    }

    private static void Emit(string name, RetroRoomDriver driver, RetroRoomDriver.Watcher watcher) {
        driver.AssertConverged();
        var fresh = new SnapFrame(
            driver.Room,
            RetroRoomDriver.Epoch,
            driver.Seq,
            0,
            driver.Snapshot(watcher),
            driver.Fixture.Projection.Overlay(driver.Fixture.State, watcher.Who.Id));
        var freshNode = Node(fresh);
        var expected = new JsonObject { ["d"] = freshNode["d"]!.DeepClone(), ["o"] = freshNode["o"]!.DeepClone() };
        var viewer = new JsonObject {
            ["pid"] = watcher.Who.Id.ToString(),
            ["kind"] = "user",
        };

        var text = new StringBuilder();
        text.Append("{\n");
        text.Append("  \"source\": \"tests/Retroscope.Core.Tests/Retro/RetroTranscriptTests.cs\",\n");
        text.Append("  \"room\": \"").Append(driver.Room).Append("\",\n");
        text.Append("  \"viewer\": ").Append(viewer.ToJsonString()).Append(",\n");
        text.Append("  \"frames\": [\n");
        text.Append(string.Join(",\n", watcher.Frames.Select(frame => "    " + Node(frame).ToJsonString())));
        text.Append("\n  ],\n");
        text.Append("  \"expectedViewerState\": ").Append(expected.ToJsonString()).Append(",\n");
        text.Append("  \"expectedNav\": null\n");
        text.Append("}\n");

        var path = Path.Combine(RepoRoot.Path, "tests", "golden", "transcripts", name + ".json");
        var current = File.Exists(path) ? File.ReadAllText(path) : null;
        if (current != text.ToString()) {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text.ToString());
            Assert.Fail($"{name}.json was {(current is null ? "missing" : "stale")} and has been regenerated; rerun and commit it.");
        }
    }

    private static JsonNode Node(ServerFrame frame) => JsonNode.Parse(WireWriter.ToUtf8(frame))!;
}
