using Microsoft.Extensions.Time.Testing;
using Retroscope.Server.Realtime.Transport;

namespace Retroscope.Integration.Tests.Transport;

[Trait("Category", "Transport")]
public sealed class ConnRateLimitTests {
    private readonly FakeTimeProvider _time = new();

    [Fact]
    public void The_burst_is_allowed_then_frames_are_limited() {
        var limit = new ConnRateLimit(_time, 20, 40, TimeSpan.FromSeconds(10));

        var verdicts = Enumerable.Range(0, 41).Select(_ => limit.Take()).ToList();

        Assert.All(verdicts.Take(40), x => Assert.Equal(RateVerdict.Allowed, x));
        Assert.Equal(RateVerdict.Limited, verdicts[40]);
    }

    [Fact]
    public void Tokens_refill_at_the_steady_rate() {
        var limit = new ConnRateLimit(_time, 20, 40, TimeSpan.FromSeconds(10));
        Drain(limit);

        _time.Advance(TimeSpan.FromMilliseconds(100));

        Assert.Equal(RateVerdict.Allowed, limit.Take());
        Assert.Equal(RateVerdict.Allowed, limit.Take());
        Assert.Equal(RateVerdict.Limited, limit.Take());
    }

    [Fact]
    public void Sending_faster_than_the_rate_for_the_abuse_window_is_abusive() {
        var limit = new ConnRateLimit(_time, 20, 40, TimeSpan.FromSeconds(10));
        Drain(limit);

        var early = Hammer(limit, TimeSpan.FromSeconds(9.9));
        var late = Hammer(limit, TimeSpan.FromSeconds(0.2));

        Assert.DoesNotContain(RateVerdict.Abusive, early);
        Assert.Contains(RateVerdict.Abusive, late);
    }

    [Fact]
    public void A_one_second_break_ends_the_abuse_streak() {
        var limit = new ConnRateLimit(_time, 20, 40, TimeSpan.FromSeconds(10));
        Drain(limit);
        Hammer(limit, TimeSpan.FromSeconds(9));

        _time.Advance(TimeSpan.FromMilliseconds(1100));
        Drain(limit);
        var after = Hammer(limit, TimeSpan.FromSeconds(5));

        Assert.DoesNotContain(RateVerdict.Abusive, after);
    }

    [Fact]
    public void A_short_burst_over_the_limit_is_only_limited() {
        var limit = new ConnRateLimit(_time, 20, 40, TimeSpan.FromSeconds(10));

        var verdicts = Enumerable.Range(0, 100).Select(_ => limit.Take()).ToList();

        Assert.DoesNotContain(RateVerdict.Abusive, verdicts);
    }

    [Fact]
    public void A_borrowed_frame_goes_at_most_one_burst_into_debt_and_the_debt_is_repaid_first() {
        var limit = new ConnRateLimit(_time, 20, 40, TimeSpan.FromSeconds(10));
        Drain(limit);

        var borrowed = Enumerable.Range(0, 41).Select(_ => limit.Take(borrow: true)).ToList();
        _time.Advance(TimeSpan.FromSeconds(2));
        var repaid = limit.Take();
        _time.Advance(TimeSpan.FromMilliseconds(100));
        var after = limit.Take();

        Assert.All(borrowed.Take(40), x => Assert.Equal(RateVerdict.Allowed, x));
        Assert.Equal(RateVerdict.Limited, borrowed[40]);
        Assert.Equal(RateVerdict.Limited, repaid);
        Assert.Equal(RateVerdict.Allowed, after);
    }

    // Two frames every 40 ms: twice the 20/s refill, so some frames are refused in every second.
    private List<RateVerdict> Hammer(ConnRateLimit limit, TimeSpan duration) {
        var verdicts = new List<RateVerdict>();
        for (var elapsed = TimeSpan.Zero; elapsed < duration; elapsed += TimeSpan.FromMilliseconds(40)) {
            _time.Advance(TimeSpan.FromMilliseconds(40));
            verdicts.Add(limit.Take());
            verdicts.Add(limit.Take());
        }
        return verdicts;
    }

    private static void Drain(ConnRateLimit limit) {
        while (limit.Take() == RateVerdict.Allowed) {
        }
    }
}
