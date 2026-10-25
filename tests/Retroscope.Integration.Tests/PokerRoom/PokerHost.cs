using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Data;
using Retroscope.Server.Hosting;
using Retroscope.Server.Realtime.Actors;
using Retroscope.Server.Realtime.Rooms;
using Retroscope.Server.Realtime.Stores;
using Retroscope.Server.Realtime.Transport;

namespace Retroscope.Integration.Tests.PokerRoom;

/// <summary>
/// The real server modules a poker room needs (hosting, database, leader, actors, transport, poker room) on Kestrel
/// loopback against a migrated test database. Time is fake, so warm-up, auto-reveal and settle timers run on demand.
/// </summary>
internal sealed class PokerHost : IAsyncDisposable {
    public const string Origin = "https://poker.test";
    public const string CallerHeader = "X-Test-Caller";

    private PokerHost(WebApplication app, TestDatabase database, FakeTimeProvider time, Uri socketUri) {
        App = app;
        Database = database;
        Time = time;
        SocketUri = socketUri;
    }

    public WebApplication App { get; }

    public TestDatabase Database { get; }

    public FakeTimeProvider Time { get; }

    public Uri SocketUri { get; }

    public ActorRegistry Registry => App.Services.GetRequiredService<ActorRegistry>();

    public PokerStore Store => App.Services.GetRequiredService<PokerStore>();

    public PokerJiraLoop Jira => App.Services.GetRequiredService<PokerJiraLoop>();

    public static CancellationToken Token => TestContext.Current.CancellationToken;

    public static async Task<PokerHost> StartAsync(
        TestDatabase database,
        FakeTimeProvider time,
        TestAccess access,
        FakeJiraPort? jira = null,
        FakeTeamNotices? notices = null) {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?> {
            ["PUBLIC_URL"] = Origin,
            ["WEB_ROOT"] = Path.Combine(Path.GetTempPath(), "rs-no-web-build"),
            ["DATABASE_URL"] = database.AppConnectionString,
            ["DATABASE_OWNER_URL"] = database.OwnerConnectionString,
            [LeaderOptions.AcquireSecondsKey] = "20",
        });
        var services = builder.Services;
        services.AddSingleton<TimeProvider>(time);
        HostingModule.AddServices(services, builder.Configuration);
        Db.AddServices(services, builder.Configuration);
        Leader.AddServices(services, builder.Configuration);
        ActorsModule.AddServices(services, builder.Configuration);
        TransportModule.AddServices(services, builder.Configuration);
        Server.Realtime.Rooms.PokerRoom.AddServices(services, builder.Configuration);
        // Fake time never refills the per-connection token bucket, so the tests get a large one.
        services.Replace(ServiceDescriptor.Singleton(new TransportOptions { FrameBurst = 10_000 }));
        services.Replace(ServiceDescriptor.Singleton<IAccessView>(access));
        services.AddSingleton<ICallerResolver, HeaderCaller>();
        if (jira is not null) {
            services.AddSingleton<IPokerJiraPort>(jira);
        }
        if (notices is not null) {
            services.AddSingleton<ITeamNotices>(notices);
        }

        var app = builder.Build();
        HostingModule.UsePipeline(app);
        HostingModule.Map(app);
        ActorsModule.Map(app);
        TransportModule.Map(app);
        Server.Realtime.Rooms.PokerRoom.Map(app);
        await app.StartAsync(Token);

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
        var http = new Uri(address);
        return new PokerHost(app, database, time, new Uri($"ws://{http.Authority}{WsEndpoint.Path}"));
    }

    public async Task<PokerSocket> JoinAsync(Principal who, RoomKey room, ClientId? client = null) {
        var socket = await PokerSocket.ConnectAsync(this, who, client);
        await socket.SubscribeAsync(room);
        return socket;
    }

    /// <summary>Runs on the actor's thread, after everything queued before it.</summary>
    public Task<T> QueryAsync<T>(RoomKey room, Func<PokerState, T> read) {
        return Registry.QueryAsync<PokerState, PokerInput, T>(room, view => ValueTask.FromResult(read(view.State!)), Token);
    }

    /// <summary>The actor's state and a fresh load from the database describe the same persisted session.</summary>
    public async Task AssertPersistedAsync(RoomKey room) {
        var live = await QueryAsync(room, Describe);
        var loaded = await Store.LoadAsync(room, Time.GetUtcNow(), Token);
        Assert.NotNull(loaded);
        Assert.Equal(live, Describe(loaded));
    }

    /// <summary>Advances fake time and lets the actor handle whatever timers fired.</summary>
    public async Task AdvanceAsync(RoomKey room, TimeSpan by) {
        Time.Advance(by);
        await QueryAsync(room, _ => 0);
    }

    public static string Describe(PokerState state) {
        var text = new StringBuilder();
        text.AppendLine(CultureInfo.InvariantCulture, $"{state.Code} team={state.TeamId} created={state.CreatedAt:O} presence={state.PresenceEnabled} follow={state.FollowSpotlight}");
        text.AppendLine(CultureInfo.InvariantCulture, $"observers={string.Join(',', state.Observers.Select(x => x.ToString()).Order(StringComparer.Ordinal))}");
        var spotlight = state.Spotlight;
        text.AppendLine(CultureInfo.InvariantCulture, $"spotlight={spotlight?.Holder}/{spotlight?.HolderClient}/{spotlight?.RoundId}");
        text.AppendLine(CultureInfo.InvariantCulture, $"current={state.CurrentRoundId} next={state.NextRoundNumber}");
        foreach (var id in state.Order) {
            var round = state.Rounds[id];
            var info = PokerApply.Info(round);
            var jira = info.Jira is { } j ? $"{j.Points.ToString("0.##", CultureInfo.InvariantCulture)}@{j.At}/{j.By}" : "-";
            text.AppendLine(CultureInfo.InvariantCulture, $"round {info with { Jira = null }} jira={jira}");
            foreach (var (pid, vote) in round.Votes.OrderBy(x => x.Key.ToString(), StringComparer.Ordinal)) {
                text.AppendLine(CultureInfo.InvariantCulture, $"  vote {pid} {vote}");
            }
        }
        return text.ToString();
    }

    public async ValueTask DisposeAsync() {
        await App.StopAsync(CancellationToken.None);
        await App.DisposeAsync();
    }

    // "<user|guest>:<principal uuid>:<display name>"
    private sealed class HeaderCaller : ICallerResolver {
        public ValueTask<Caller?> ResolveAsync(HttpContext context) {
            if (!context.Request.Headers.TryGetValue(CallerHeader, out var header)) {
                return ValueTask.FromResult<Caller?>(null);
            }
            var parts = header.ToString().Split(':', 3);
            var kind = parts[0] == "guest" ? PrincipalKind.Guest : PrincipalKind.User;
            var principal = new Principal(new PrincipalId(Guid.Parse(parts[1])), kind, parts[2], null, false);
            return ValueTask.FromResult<Caller?>(new Caller(principal, kind == PrincipalKind.User));
        }
    }
}

internal sealed class TestAccess : IAccessView {
    private readonly ConcurrentDictionary<(TeamId, PrincipalId), TeamRole> _roles = new();

    public void Set(TeamId team, PrincipalId principal, TeamRole? role) {
        if (role is { } value) {
            _roles[(team, principal)] = value;
        } else {
            _roles.TryRemove((team, principal), out _);
        }
    }

    public TeamRole? RoleIn(TeamId team, PrincipalId principal) {
        return _roles.TryGetValue((team, principal), out var role) ? role : null;
    }
}

/// <summary>Records every call; meta comes from <see cref="Issues"/>, submits answer with <see cref="SubmitResult"/>.</summary>
internal sealed class FakeJiraPort : IPokerJiraPort {
    private readonly ConcurrentQueue<string[]> _metaCalls = new();
    private readonly ConcurrentQueue<(string Key, decimal Points)> _submits = new();

    public ConcurrentDictionary<string, JiraMeta> Issues { get; } = new(StringComparer.Ordinal);

    public JiraSubmitResult SubmitResult { get; set; } = new(true);

    /// <summary>When set, submits wait for it, so a test can look at the pending state.</summary>
    public TaskCompletionSource? SubmitGate { get; set; }

    public IReadOnlyList<string[]> MetaCalls => [.. _metaCalls];

    public IReadOnlyList<(string Key, decimal Points)> Submits => [.. _submits];

    public async Task<JiraSubmitResult> SubmitPointsAsync(
        TeamId team,
        string issueKey,
        decimal points,
        PrincipalId by,
        CancellationToken cancellationToken) {
        _submits.Enqueue((issueKey, points));
        if (SubmitGate is { } gate) {
            await gate.Task.WaitAsync(cancellationToken);
        }
        return SubmitResult;
    }

    public Task<IReadOnlyList<JiraMeta>> LoadMetaAsync(
        TeamId team,
        IReadOnlyCollection<string> issueKeys,
        CancellationToken cancellationToken) {
        _metaCalls.Enqueue([.. issueKeys]);
        IReadOnlyList<JiraMeta> found = [.. issueKeys.Where(Issues.ContainsKey).Select(x => Issues[x])];
        return Task.FromResult(found);
    }
}

internal sealed class FakeTeamNotices : ITeamNotices {
    private readonly ConcurrentQueue<(SessionId Session, int Pointed, int Total)> _poker = new();

    public IReadOnlyList<(SessionId Session, int Pointed, int Total)> Poker => [.. _poker];

    public void PokerChanged(TeamId team, SessionId session, int pointed, int total) => _poker.Enqueue((session, pointed, total));

    public void BoardsChanged(TeamId team, BoardId board) {
    }

    public void ActionItemCommentsChanged(TeamId team, ActionItemId item, int count) {
    }

    public void ActionItemCreated(TeamId team, ActionItemInfo item) {
    }

    public void ActionItemTextChanged(TeamId team, CardId sourceCard, string text, int cardVersion) {
    }

    public void SourceCardDeleted(TeamId team, CardId sourceCard) {
    }
}
