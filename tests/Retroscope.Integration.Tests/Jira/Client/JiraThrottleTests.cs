using Microsoft.Extensions.Time.Testing;
using Retroscope.Core.Ids;
using Retroscope.Server.Jira;

namespace Retroscope.Integration.Tests.Jira.Client;

[Trait("Category", "JiraClient")]
public sealed class JiraThrottleTests : IDisposable {
    private readonly FakeTimeProvider _time = new();
    private readonly List<TimeSpan> _delays = [];
    private readonly JiraThrottle _throttle;

    public JiraThrottleTests() {
        _throttle = new JiraThrottle(new JiraThrottleOptions(), _time, (wait, _) => {
            lock (_delays) {
                _delays.Add(wait);
            }
            _time.Advance(wait);
            return Task.CompletedTask;
        });
    }

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => _throttle.Dispose();

    [Fact]
    public async Task A_burst_of_16_passes_then_requests_are_paced_at_8_per_second() {
        var team = new TeamId(Guid.NewGuid());

        for (var i = 0; i < 24; i++) {
            using var lease = await _throttle.AcquireAsync(team, Ct);
        }

        Assert.Equal(8, _delays.Count);
        Assert.All(_delays, d => Assert.Equal(TimeSpan.FromMilliseconds(125), d));
    }

    [Fact]
    public async Task The_bucket_refills_over_time() {
        var team = new TeamId(Guid.NewGuid());
        for (var i = 0; i < 16; i++) {
            using var lease = await _throttle.AcquireAsync(team, Ct);
        }

        _time.Advance(TimeSpan.FromSeconds(1));
        for (var i = 0; i < 8; i++) {
            using var lease = await _throttle.AcquireAsync(team, Ct);
        }

        Assert.Empty(_delays);
    }

    [Fact]
    public async Task Teams_have_their_own_buckets() {
        for (var i = 0; i < 16; i++) {
            using var a = await _throttle.AcquireAsync(new TeamId(Guid.NewGuid()), Ct);
        }

        Assert.Empty(_delays);
    }

    [Fact]
    public async Task At_most_4_requests_of_one_team_run_at_once() {
        var team = new TeamId(Guid.NewGuid());
        var held = new List<IDisposable>();
        for (var i = 0; i < 4; i++) {
            held.Add(await _throttle.AcquireAsync(team, Ct));
        }

        var fifth = _throttle.AcquireAsync(team, Ct).AsTask();
        await Task.Delay(50, Ct);
        Assert.False(fifth.IsCompleted);

        held[0].Dispose();
        (await fifth).Dispose();
        held.Skip(1).ToList().ForEach(l => l.Dispose());
    }

    [Fact]
    public async Task At_most_16_requests_run_at_once_across_teams() {
        var held = new List<IDisposable>();
        for (var t = 0; t < 4; t++) {
            var team = new TeamId(Guid.NewGuid());
            for (var i = 0; i < 4; i++) {
                held.Add(await _throttle.AcquireAsync(team, Ct));
            }
        }

        var seventeenth = _throttle.AcquireAsync(new TeamId(Guid.NewGuid()), Ct).AsTask();
        await Task.Delay(50, Ct);
        Assert.False(seventeenth.IsCompleted);

        held[5].Dispose();
        (await seventeenth).Dispose();
        held.ForEach(l => l.Dispose());
    }

    [Fact]
    public async Task A_cancelled_wait_gives_its_team_slot_back() {
        var team = new TeamId(Guid.NewGuid());
        var others = new List<IDisposable>();
        for (var t = 0; t < 3; t++) {
            var other = new TeamId(Guid.NewGuid());
            for (var i = 0; i < 4; i++) {
                others.Add(await _throttle.AcquireAsync(other, Ct));
            }
        }
        var mine = new List<IDisposable>();
        for (var i = 0; i < 4; i++) {
            mine.Add(await _throttle.AcquireAsync(team, Ct));
        }
        mine[0].Dispose();
        using var cancel = new CancellationTokenSource();

        var waiting = _throttle.AcquireAsync(new TeamId(Guid.NewGuid()), Ct).AsTask();
        var blocked = _throttle.AcquireAsync(team, cancel.Token).AsTask();
        await cancel.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => blocked);

        others[0].Dispose();
        (await waiting).Dispose();
        (await _throttle.AcquireAsync(team, Ct)).Dispose();
        others.Skip(1).Concat(mine.Skip(1)).ToList().ForEach(l => l.Dispose());
    }

    [Fact]
    public void Successes_reset_the_breaker_count() {
        var team = new TeamId(Guid.NewGuid());
        for (var i = 0; i < 4; i++) {
            _throttle.RecordFailure(team);
        }
        _throttle.RecordSuccess(team);
        for (var i = 0; i < 4; i++) {
            _throttle.RecordFailure(team);
        }

        Assert.False(_throttle.IsOpen(team));
        _throttle.RecordFailure(team);
        Assert.True(_throttle.IsOpen(team));
        Assert.Equal(JiraErrorCode.Unavailable,
            Assert.Throws<JiraException>(() => _throttle.ThrowIfOpen(team)).Error.Code);
    }
}
