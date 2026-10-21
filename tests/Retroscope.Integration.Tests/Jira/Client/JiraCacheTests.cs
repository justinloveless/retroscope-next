using Microsoft.Extensions.Time.Testing;
using Retroscope.Core.Ids;
using Retroscope.Server.Jira;

namespace Retroscope.Integration.Tests.Jira.Client;

[Trait("Category", "JiraClient")]
public sealed class JiraCacheTests {
    private static readonly TimeSpan Minute = TimeSpan.FromMinutes(1);
    private static readonly int[] Numbers = [1];

    private readonly FakeTimeProvider _time = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Entries_expire_after_their_time_to_live() {
        var cache = new JiraCache(_time);
        cache.Set("a", "value", 10, TimeSpan.FromSeconds(30));

        Assert.True(cache.TryGet<string>("a", out _));
        _time.Advance(TimeSpan.FromSeconds(30));
        Assert.False(cache.TryGet<string>("a", out _));
        Assert.Equal(0, cache.SizeBytes);
    }

    [Fact]
    public void The_least_recently_used_entries_go_when_the_size_cap_is_passed() {
        var cache = new JiraCache(_time, capacityBytes: 1000);
        cache.Set("a", "A", 300, Minute);
        cache.Set("b", "B", 300, Minute);
        cache.Set("c", "C", 300, Minute);
        Assert.True(cache.TryGet<string>("a", out _));

        cache.Set("d", "D", 300, Minute);

        Assert.True(cache.TryGet<string>("a", out _));
        Assert.False(cache.TryGet<string>("b", out _));
        Assert.True(cache.TryGet<string>("c", out _));
        Assert.True(cache.TryGet<string>("d", out _));
        Assert.True(cache.SizeBytes <= cache.CapacityBytes);
    }

    [Fact]
    public void The_default_cap_is_8_MB_and_an_oversized_value_is_not_stored() {
        var cache = new JiraCache(_time);
        Assert.Equal(8L * 1024 * 1024, cache.CapacityBytes);

        cache.Set("big", "x", 9L * 1024 * 1024, Minute);

        Assert.False(cache.TryGet<string>("big", out _));
        Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void A_tag_drops_every_entry_that_carries_it() {
        var team = new TeamId(Guid.NewGuid());
        var cache = new JiraCache(_time);
        cache.Set("browse-1", "rows", 10, Minute, [JiraCache.IssueTag(team, "DUN-1"), JiraCache.IssueTag(team, "DUN-2")]);
        cache.Set("browse-2", "rows", 10, Minute, [JiraCache.IssueTag(team, "DUN-2")]);
        cache.Set("meta-3", "meta", 10, Minute, [JiraCache.IssueTag(team, "DUN-3")]);

        cache.InvalidateTag(JiraCache.IssueTag(team, "DUN-2"));

        Assert.False(cache.TryGet<string>("browse-1", out _));
        Assert.False(cache.TryGet<string>("browse-2", out _));
        Assert.True(cache.TryGet<string>("meta-3", out _));
    }

    [Fact]
    public async Task Concurrent_misses_run_the_factory_once() {
        var cache = new JiraCache(_time);
        var calls = 0;
        var release = new TaskCompletionSource();

        var readers = Enumerable.Range(0, 8).Select(_ => cache.GetOrCreateAsync("k", Minute, async () => {
            Interlocked.Increment(ref calls);
            await release.Task;
            return ("value", 5L, (IReadOnlyCollection<string>?)null);
        }, Ct)).ToList();
        release.SetResult();

        Assert.All(await Task.WhenAll(readers), v => Assert.Equal("value", v));
        Assert.Equal(1, calls);
        Assert.True(cache.TryGet<string>("k", out _));
    }

    [Fact]
    public async Task Callers_of_one_key_with_different_types_do_not_disturb_each_others_flights() {
        var cache = new JiraCache(_time);
        var releaseText = new TaskCompletionSource();
        var releaseNumbers = new TaskCompletionSource();
        var numberCalls = 0;
        Task<(int[] Value, long SizeBytes, IReadOnlyCollection<string>? Tags)> FetchNumbers() {
            Interlocked.Increment(ref numberCalls);
            return releaseNumbers.Task.ContinueWith(_ => (Numbers, 4L, (IReadOnlyCollection<string>?)null), TaskScheduler.Default);
        }

        var text = cache.GetOrCreateAsync("k", Minute, async () => {
            await releaseText.Task;
            return ("value", 5L, (IReadOnlyCollection<string>?)null);
        }, Ct);
        var first = cache.GetOrCreateAsync("k", Minute, FetchNumbers, Ct);
        releaseText.SetResult();
        Assert.Equal("value", await text);
        var second = cache.GetOrCreateAsync("k", Minute, FetchNumbers, Ct);

        Assert.True(cache.TryGet<string>("k", out _));
        releaseNumbers.SetResult();
        Assert.Same(Numbers, await first);
        Assert.Same(Numbers, await second);
        Assert.Equal(1, numberCalls);
    }

    [Fact]
    public async Task A_value_invalidated_while_it_was_fetched_is_not_stored() {
        var team = new TeamId(Guid.NewGuid());
        var cache = new JiraCache(_time);
        var release = new TaskCompletionSource();
        var started = new TaskCompletionSource();

        var read = cache.GetOrCreateAsync("k", Minute, async () => {
            started.SetResult();
            await release.Task;
            return ("stale", 5L, (IReadOnlyCollection<string>?)[JiraCache.IssueTag(team, "DUN-1")]);
        }, Ct);
        await started.Task;
        cache.InvalidateTag(JiraCache.IssueTag(team, "DUN-1"));
        release.SetResult();

        Assert.Equal("stale", await read);
        Assert.False(cache.TryGet<string>("k", out _));
    }

    [Fact]
    public async Task A_failed_fetch_is_not_cached() {
        var cache = new JiraCache(_time);

        await Assert.ThrowsAsync<JiraException>(() => cache.GetOrCreateAsync<string>(
            "k", Minute, () => throw new JiraException(JiraError.Unavailable), Ct));

        Assert.Equal("ok", await cache.GetOrCreateAsync("k", Minute,
            () => Task.FromResult(("ok", 2L, (IReadOnlyCollection<string>?)null)), Ct));
    }
}
