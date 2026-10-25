using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using static Retroscope.Core.Tests.Retro.RetroFixture;

namespace Retroscope.Core.Tests.Retro;

[Trait("Category", "Retro")]
public sealed class TimerTests {
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);

    [Fact]
    public void Start_runs_on_the_server_clock() {
        var fx = new RetroFixture();
        var events = fx.Do(Alice, new RetroTimerStart(300_000, Alarm: true, Music: true, TimerState.Idle));

        var changed = Assert.IsType<TimerChanged>(Assert.Single(events));
        Assert.Null(changed.ExpiredAt);
        Assert.Equal(new TimerValue(TimerState.Running, 300_000, true, true, EndsAt: Start.AddMinutes(5)), fx.State.Timer);
    }

    [Fact]
    public void Every_move_is_compare_and_set_on_from() {
        var fx = new RetroFixture();
        RetroCommand[] wrongFrom = [
            new RetroTimerStart(60_000, true, false, TimerState.Running),
            new RetroTimerPause(TimerState.Running),
            new RetroTimerResume(TimerState.Paused),
            new RetroTimerReset(TimerState.Paused),
        ];
        foreach (var command in wrongFrom) {
            var reject = fx.Rejects(Alice, command, NackCode.Stale);
            Assert.Equal("idle", reject.Data!.Value.GetProperty("current").GetProperty("state").GetString());
        }
    }

    [Fact]
    public void Pause_keeps_the_remainder_and_resume_extends_from_now() {
        var fx = new RetroFixture();
        fx.Do(Alice, new RetroTimerStart(300_000, true, false, TimerState.Idle));
        fx.Now += Minute;
        fx.Do(Bob, new RetroTimerPause(TimerState.Running));
        Assert.Equal(new TimerValue(TimerState.Paused, 300_000, true, false, RemainingMs: 240_000), fx.State.Timer);

        fx.Now += Minute * 10;
        fx.Do(Alice, new RetroTimerResume(TimerState.Paused));
        Assert.Equal(fx.Now.AddMinutes(4), fx.State.Timer.EndsAt);
        Assert.Null(fx.State.Timer.RemainingMs);
    }

    [Fact]
    public void Moves_that_are_not_open_from_the_current_state() {
        var fx = new RetroFixture();
        fx.Rejects(Alice, new RetroTimerPause(TimerState.Idle), NackCode.Invalid);
        fx.Rejects(Alice, new RetroTimerResume(TimerState.Idle), NackCode.Invalid);
        fx.NoOp(Alice, new RetroTimerReset(TimerState.Idle));

        fx.Do(Alice, new RetroTimerStart(60_000, true, false, TimerState.Idle));
        fx.NoOp(Alice, new RetroTimerResume(TimerState.Running));
        fx.Do(Alice, new RetroTimerPause(TimerState.Running));
        fx.NoOp(Alice, new RetroTimerPause(TimerState.Paused));
    }

    [Fact]
    public void Start_restarts_a_running_or_paused_timer() {
        var fx = new RetroFixture();
        fx.Do(Alice, new RetroTimerStart(60_000, true, false, TimerState.Idle));
        fx.Do(Alice, new RetroTimerPause(TimerState.Running));
        fx.Do(Alice, new RetroTimerStart(120_000, false, false, TimerState.Paused));
        Assert.Equal(new TimerValue(TimerState.Running, 120_000, false, false, EndsAt: Start.AddMinutes(2)), fx.State.Timer);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(7_200_001)]
    [InlineData(-5)]
    public void Durations_outside_the_column_range_are_invalid(int durationMs) {
        var fx = new RetroFixture();
        fx.Rejects(Alice, new RetroTimerStart(durationMs, true, false, TimerState.Idle), NackCode.Invalid);
    }

    [Fact]
    public void Reset_returns_to_idle_and_keeps_the_duration() {
        var fx = new RetroFixture();
        fx.Do(Alice, new RetroTimerStart(60_000, true, false, TimerState.Idle));
        fx.Do(Alice, new RetroTimerReset(TimerState.Running));
        Assert.Equal(new TimerValue(TimerState.Idle, 60_000, true, false), fx.State.Timer);
    }

    [Fact]
    public void Flags_are_set_semantics_and_keep_the_running_end() {
        var fx = new RetroFixture();
        fx.Do(Alice, new RetroTimerStart(60_000, true, false, TimerState.Idle));
        fx.Do(Bob, new RetroTimerSetFlags(Music: true));
        Assert.Equal(new TimerValue(TimerState.Running, 60_000, true, true, EndsAt: Start.AddMinutes(1)), fx.State.Timer);
        fx.NoOp(Bob, new RetroTimerSetFlags(Alarm: true, Music: true));
        fx.NoOp(Bob, new RetroTimerSetFlags());
    }

    [Fact]
    public void A_running_timer_asks_for_one_timer_at_its_end() {
        var fx = new RetroFixture();
        Assert.Empty(fx.Decider.Timers(fx.State, fx.Presence, fx.Now));

        fx.Do(Alice, new RetroTimerStart(60_000, true, false, TimerState.Idle));
        var request = Assert.Single(fx.Decider.Timers(fx.State, fx.Presence, fx.Now));
        Assert.Equal(Start.AddMinutes(1), request.DueAt);
        Assert.Equal(new RetroInput.TimerDue(Start.AddMinutes(1)), request.Command);

        fx.Do(Alice, new RetroTimerPause(TimerState.Running));
        Assert.Empty(fx.Decider.Timers(fx.State, fx.Presence, fx.Now));
    }

    [Fact]
    public void Expiry_happens_once_and_says_so() {
        var fx = new RetroFixture();
        fx.Do(Alice, new RetroTimerStart(60_000, true, false, TimerState.Idle));
        var due = fx.Decider.Timers(fx.State, fx.Presence, fx.Now)[0].Command;

        fx.NoOp(null, due);
        fx.Now = Start.AddMinutes(1);
        var changed = Assert.IsType<TimerChanged>(Assert.Single(fx.Do(null, due)));
        Assert.Equal(Start.AddMinutes(1), changed.ExpiredAt);
        Assert.Equal(TimerState.Idle, fx.State.Timer.State);

        var wire = Assert.IsType<RetroTimer>(Assert.Single(fx.Projection.ForViewer(fx.State, [changed], Bob.Id)));
        Assert.Equal(new RetroTimer(TimerState.Idle, 60_000, true, false, EndsAt: Start.AddMinutes(1).ToUnixTimeMilliseconds(), Expired: true), wire);

        fx.NoOp(null, due);
    }

    [Fact]
    public void A_stale_due_from_an_earlier_run_is_ignored() {
        var fx = new RetroFixture();
        fx.Do(Alice, new RetroTimerStart(60_000, true, false, TimerState.Idle));
        var firstDue = fx.Decider.Timers(fx.State, fx.Presence, fx.Now)[0].Command;
        fx.Now += TimeSpan.FromSeconds(30);
        fx.Do(Alice, new RetroTimerStart(60_000, true, false, TimerState.Running));

        fx.Now = Start.AddMinutes(1);
        fx.NoOp(null, firstDue);
        Assert.Equal(TimerState.Running, fx.State.Timer.State);
    }

    [Fact]
    public void A_timer_that_ended_while_down_is_due_at_once_and_expires_once() {
        var fx = new RetroFixture();
        fx.State.Timer = new TimerValue(TimerState.Running, 60_000, true, false, EndsAt: Start.AddMinutes(-3));

        var request = Assert.Single(fx.Decider.Timers(fx.State, fx.Presence, fx.Now));
        Assert.True(request.DueAt <= fx.Now);
        var changed = Assert.IsType<TimerChanged>(Assert.Single(fx.Do(null, request.Command)));
        Assert.Equal(Start.AddMinutes(-3), changed.ExpiredAt);
        Assert.Empty(fx.Decider.Timers(fx.State, fx.Presence, fx.Now));
    }

    [Fact]
    public void Pausing_after_the_end_expires_instead() {
        var fx = new RetroFixture();
        fx.Do(Alice, new RetroTimerStart(60_000, true, false, TimerState.Idle));
        fx.Now = Start.AddMinutes(2);
        var changed = Assert.IsType<TimerChanged>(Assert.Single(fx.Do(Alice, new RetroTimerPause(TimerState.Running))));
        Assert.Equal(Start.AddMinutes(1), changed.ExpiredAt);
        Assert.Equal(TimerState.Idle, fx.State.Timer.State);
    }

    [Fact]
    public void Only_members_run_the_timer() {
        var fx = new RetroFixture();
        fx.Rejects(Carol, new RetroTimerStart(60_000, true, false, TimerState.Idle), NackCode.Forbidden);
        fx.Rejects(Gus, new RetroTimerSetFlags(Music: true), NackCode.Forbidden);
        fx.Do(Bob, new RetroTimerStart(60_000, true, false, TimerState.Idle));
    }

    [Fact]
    public void Paused_snapshots_carry_the_remainder_and_running_ones_only_the_end() {
        var running = new TimerValue(TimerState.Running, 60_000, true, true, EndsAt: Start);
        Assert.Equal(new RetroTimer(TimerState.Running, 60_000, true, true, EndsAt: Start.ToUnixTimeMilliseconds()), TimerRules.ToWire(running));
        var paused = new TimerValue(TimerState.Paused, 60_000, true, true, RemainingMs: 1234);
        Assert.Equal(new RetroTimer(TimerState.Paused, 60_000, true, true, RemainingMs: 1234), TimerRules.ToWire(paused));
    }
}
