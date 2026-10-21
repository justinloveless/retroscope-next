using Retroscope.Core.Protocol;

namespace Retroscope.Core.Retro;

public enum TimerVerdict : byte {
    Changed,
    Unchanged,

    /// <summary>The client's <c>from</c> is not the current state (compare-and-set, R5).</summary>
    Stale,

    /// <summary>The move is not open from the current state, or the duration is out of range.</summary>
    Invalid,
}

/// <param name="ExpiredAt">Set when the step ended a running timer whose end had already passed.</param>
public readonly record struct TimerStep(TimerVerdict Verdict, TimerValue? Next = null, DateTimeOffset? ExpiredAt = null) {
    public static readonly TimerStep Unchanged = new(TimerVerdict.Unchanged);
    public static readonly TimerStep Stale = new(TimerVerdict.Stale);
    public static readonly TimerStep Invalid = new(TimerVerdict.Invalid);

    public static TimerStep To(TimerValue next, DateTimeOffset? expiredAt = null) => new(TimerVerdict.Changed, next, expiredAt);
}

/// <summary>
/// The shared timer as a compare-and-set state machine on the server clock (DESIGN §c.4, R16):
/// idle → running (start), running → paused (pause), paused → running (resume), any → idle (reset or expiry).
/// Start is also open from running and paused, as a restart.
/// </summary>
public static class TimerRules {
    public const int MinDurationMs = 1_000;
    public const int MaxDurationMs = 7_200_000;

    public static TimerStep Start(TimerValue timer, TimerState from, int durationMs, bool alarm, bool music, DateTimeOffset now) {
        if (from != timer.State) {
            return TimerStep.Stale;
        }
        if (durationMs is < MinDurationMs or > MaxDurationMs) {
            return TimerStep.Invalid;
        }
        return TimerStep.To(new TimerValue(TimerState.Running, durationMs, alarm, music, EndsAt: now.AddMilliseconds(durationMs)));
    }

    public static TimerStep Pause(TimerValue timer, TimerState from, DateTimeOffset now) {
        if (from != timer.State) {
            return TimerStep.Stale;
        }
        if (timer.State != TimerState.Running) {
            return timer.State == TimerState.Paused ? TimerStep.Unchanged : TimerStep.Invalid;
        }
        var remaining = (long)Math.Ceiling((timer.EndsAt!.Value - now).TotalMilliseconds);
        if (remaining <= 0) {
            return TimerStep.To(Idle(timer), timer.EndsAt);
        }
        return TimerStep.To(timer with { State = TimerState.Paused, EndsAt = null, RemainingMs = (int)remaining });
    }

    public static TimerStep Resume(TimerValue timer, TimerState from, DateTimeOffset now) {
        if (from != timer.State) {
            return TimerStep.Stale;
        }
        if (timer.State != TimerState.Paused) {
            return timer.State == TimerState.Running ? TimerStep.Unchanged : TimerStep.Invalid;
        }
        return TimerStep.To(timer with {
            State = TimerState.Running,
            EndsAt = now.AddMilliseconds(timer.RemainingMs!.Value),
            RemainingMs = null,
        });
    }

    public static TimerStep Reset(TimerValue timer, TimerState from) {
        if (from != timer.State) {
            return TimerStep.Stale;
        }
        return timer.State == TimerState.Idle ? TimerStep.Unchanged : TimerStep.To(Idle(timer));
    }

    public static TimerStep SetFlags(TimerValue timer, bool? alarm, bool? music) {
        var next = timer with { Alarm = alarm ?? timer.Alarm, Music = music ?? timer.Music };
        return next == timer ? TimerStep.Unchanged : TimerStep.To(next);
    }

    /// <summary>Ends the timer once its end has passed; a timer restarted or stopped since is left alone.</summary>
    public static TimerStep Expire(TimerValue timer, DateTimeOffset endsAt, DateTimeOffset now) {
        if (timer.State != TimerState.Running || timer.EndsAt != endsAt || now < endsAt) {
            return TimerStep.Unchanged;
        }
        return TimerStep.To(Idle(timer), endsAt);
    }

    /// <summary>
    /// <c>remainingMs</c> is sent only while paused: a running timer's remainder is <c>endsAt</c> minus the
    /// client's offset-corrected clock, so a snapshot never depends on when it was built.
    /// </summary>
    public static RetroTimer ToWire(TimerValue timer, DateTimeOffset? expiredAt = null) {
        if (expiredAt is { } end) {
            return new RetroTimer(TimerState.Idle, timer.DurationMs, timer.Alarm, timer.Music, EndsAt: end.ToUnixTimeMilliseconds(), Expired: true);
        }
        return new RetroTimer(
            timer.State,
            timer.DurationMs,
            timer.Alarm,
            timer.Music,
            EndsAt: timer.EndsAt?.ToUnixTimeMilliseconds(),
            RemainingMs: timer.RemainingMs);
    }

    private static TimerValue Idle(TimerValue timer) => timer with { State = TimerState.Idle, EndsAt = null, RemainingMs = null };
}
