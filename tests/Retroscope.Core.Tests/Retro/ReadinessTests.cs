using System.Text.Json;
using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using static Retroscope.Core.Tests.Retro.RetroFixture;

namespace Retroscope.Core.Tests.Retro;

[Trait("Category", "Retro")]
public sealed class ReadinessTests {
    [Fact]
    public void Ready_is_set_semantics_for_the_current_stage() {
        var fx = new RetroFixture();
        fx.EnableStages(RetroStage.Thinking);

        fx.Do(Gus, new RetroReadySet(RetroStage.Thinking, true));
        fx.NoOp(Gus, new RetroReadySet(RetroStage.Thinking, true));
        Assert.Contains(Gus.Id, fx.State.ReadyThisStage);
        fx.Do(Gus, new RetroReadySet(RetroStage.Thinking, false));
        Assert.Empty(fx.State.ReadyThisStage);
    }

    [Fact]
    public void Ready_for_another_stage_is_stale_and_carries_the_current_stage() {
        var fx = new RetroFixture();
        fx.EnableStages(RetroStage.Voting);

        var reject = fx.Rejects(Bob, new RetroReadySet(RetroStage.Thinking, true), NackCode.Stale);
        Assert.Equal("voting", reject.Data!.Value.GetProperty("current").GetString());
    }

    [Fact]
    public void A_stage_change_clears_readiness() {
        var fx = new RetroFixture();
        fx.EnableStages(RetroStage.Thinking);
        fx.Do(Bob, new RetroReadySet(RetroStage.Thinking, true));

        var events = fx.Do(Alice, new RetroStageSet(RetroStage.Thinking, RetroStage.Voting));

        Assert.Equal(new StageChanged(RetroStage.Thinking, RetroStage.Voting), Assert.Single(events));
        Assert.Empty(fx.State.ReadyThisStage);
        Assert.Equal(RetroStage.Voting, fx.State.Stage);
    }

    [Fact]
    public void Stage_set_is_compare_and_set_on_from() {
        var fx = new RetroFixture();
        fx.EnableStages(RetroStage.Voting);

        var reject = fx.Rejects(Alice, new RetroStageSet(RetroStage.Thinking, RetroStage.Voting), NackCode.Stale);
        Assert.Equal("voting", reject.Data!.Value.GetProperty("current").GetString());
        fx.NoOp(Alice, new RetroStageSet(RetroStage.Voting, RetroStage.Voting));
    }

    [Fact]
    public void Without_enforcement_facilitators_jump_to_any_stage() {
        var fx = new RetroFixture();
        fx.EnableStages(RetroStage.Thinking);
        fx.Do(Alice, new RetroStageSet(RetroStage.Thinking, RetroStage.Closed));
        fx.Do(Alice, new RetroStageSet(RetroStage.Closed, RetroStage.Voting));
    }

    [Fact]
    public void Enforced_readiness_advances_only_when_everyone_present_is_ready() {
        var fx = new RetroFixture();
        fx.EnableStages(RetroStage.Thinking, enforceReadiness: true);
        var advance = new RetroStageSet(RetroStage.Thinking, RetroStage.Voting);

        foreach (var who in new[] { Alice, Bob, Carol }) {
            fx.Do(who, new RetroReadySet(RetroStage.Thinking, true));
        }
        fx.Rejects(Alice, advance, NackCode.StageForbids);

        fx.Do(Gus, new RetroReadySet(RetroStage.Thinking, true));
        fx.Do(Alice, advance);
    }

    [Fact]
    public void Absent_principals_do_not_block_and_absent_ready_ones_do_not_count() {
        var fx = new RetroFixture();
        fx.EnableStages(RetroStage.Thinking, enforceReadiness: true);
        fx.Do(Carol, new RetroReadySet(RetroStage.Thinking, true));
        fx.Present = [Alice, Bob];

        fx.Rejects(Alice, new RetroStageSet(RetroStage.Thinking, RetroStage.Voting), NackCode.StageForbids);
        fx.Do(Alice, new RetroReadySet(RetroStage.Thinking, true));
        fx.Do(Bob, new RetroReadySet(RetroStage.Thinking, true));
        fx.Do(Alice, new RetroStageSet(RetroStage.Thinking, RetroStage.Voting));
    }

    [Fact]
    public void Enforced_readiness_refuses_skipping_and_going_back_one_stage() {
        var fx = new RetroFixture();
        fx.EnableStages(RetroStage.Voting, enforceReadiness: true);
        fx.Present = [];

        fx.Rejects(Alice, new RetroStageSet(RetroStage.Voting, RetroStage.Closed), NackCode.StageForbids);
        fx.State.Stage = RetroStage.Discussing;
        fx.Rejects(Alice, new RetroStageSet(RetroStage.Discussing, RetroStage.Voting), NackCode.StageForbids);
    }

    [Theory]
    [InlineData(RetroStage.Voting, RetroStage.Thinking)]
    [InlineData(RetroStage.Discussing, RetroStage.Thinking)]
    [InlineData(RetroStage.Closed, RetroStage.Thinking)]
    [InlineData(RetroStage.Closed, RetroStage.Discussing)]
    public void Reset_restart_and_reopen_are_always_open(RetroStage from, RetroStage to) {
        var fx = new RetroFixture();
        fx.EnableStages(from, enforceReadiness: true);
        fx.Do(Alice, new RetroStageSet(from, to));
    }

    [Fact]
    public void Readiness_is_projected_sorted_for_the_new_stage() {
        var fx = new RetroFixture();
        fx.EnableStages(RetroStage.Thinking);
        fx.Do(Bob, new RetroReadySet(RetroStage.Thinking, true));
        var events = fx.Do(Alice, new RetroReadySet(RetroStage.Thinking, true));

        var wire = Assert.IsType<RetroReadiness>(Assert.Single(fx.Projection.ForViewer(fx.State, events, Carol.Id, false)));
        Assert.Equal(RetroStage.Thinking, wire.Stage);
        Assert.Equal(new[] { Alice.Id, Bob.Id }.OrderBy(p => p.Value), wire.Ready);

        var stage = fx.Do(Alice, new RetroStageSet(RetroStage.Thinking, RetroStage.Voting));
        var projected = fx.Projection.ForViewer(fx.State, stage, Carol.Id, false);
        Assert.Equal(new RetroStageChanged(RetroStage.Voting), projected[0]);
        var cleared = Assert.IsType<RetroReadiness>(projected[1]);
        Assert.Equal(RetroStage.Voting, cleared.Stage);
        Assert.Empty(cleared.Ready);
    }

    [Fact]
    public void Stale_data_is_a_current_object() {
        var fx = new RetroFixture();
        var reject = fx.Rejects(Alice, new RetroStageSet(RetroStage.Closed, RetroStage.Thinking), NackCode.Stale);
        Assert.Equal(JsonValueKind.Object, reject.Data!.Value.ValueKind);
    }
}
