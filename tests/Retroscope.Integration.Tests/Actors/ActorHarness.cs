using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Realtime.Actors;

namespace Retroscope.Integration.Tests.Actors;

internal static class ActorHarness {
    public static readonly DateTimeOffset Start = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(10);

    public static CancellationToken Token => TestContext.Current.CancellationToken;

    public static RoomKey Room { get; } = RoomKey.Poker(new SessionId(Guid.Parse("00000000-0000-4000-8000-0000000000a0")));

    public static Viewer Alice { get; } = Viewer(1, "Alice");

    public static Viewer Bob { get; } = Viewer(2, "Bob");

    public static Viewer Carol { get; } = Viewer(3, "Carol");

    public static Viewer Viewer(int n, string name, int tab = 0, int connection = 0, PrincipalKind kind = PrincipalKind.User) {
        var principal = new Principal(new PrincipalId(Id(n)), kind, name, null, false);
        return new Viewer(principal, new ClientId(Id(0x500 + (n * 10) + tab)), new ConnectionId(Id(0x900 + (n * 100) + (tab * 10) + connection)));
    }

    public static Guid Id(int n) => new($"00000000-0000-4000-8000-{n:x12}");

    public static ActorRuntime Runtime(FakeTimeProvider time, ActorOptions? options = null) {
        return new ActorRuntime(options ?? ActorOptions.Default, time, NullLoggerFactory.Instance);
    }

    public static FakeTimeProvider Clock() => new(Start);

    /// <summary>A command frame as the transport hands it over: parsed from the wire, so it carries its payload hash.</summary>
    public static CmdFrame Cmd(long q, Command command, RoomKey? room = null) {
        var bytes = WireWriter.ToUtf8(new CmdFrame(room ?? Room, q, command));
        var parsed = WireParser.ParseClient(bytes);
        return Assert.IsType<CmdFrame>(parsed.Frame);
    }

    public static RoundId Round(int n) => new(Id(0x1000 + n));
}

/// <summary>A connection that keeps every frame it is sent, through the real byte-bounded buffer.</summary>
internal sealed class TestPeer(Viewer viewer, long capacityBytes = OutboundBuffer.DefaultCapacityBytes) : IRoomPeer {
    private readonly TaskCompletionSource _restarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _restarts;

    public Viewer Viewer { get; } = viewer;

    public ConnectionId Id => Viewer.Conn;

    public OutboundBuffer Buffer { get; } = new(capacityBytes);

    public int Restarts => Volatile.Read(ref _restarts);

    public Task Restarted => _restarted.Task;

    public bool Send(byte[] frame) => Buffer.TryEnqueue(frame);

    public void Restart() {
        Interlocked.Increment(ref _restarts);
        _restarted.TrySetResult();
    }

    public async Task<ServerFrame> NextAsync() {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ActorHarness.Token);
        timeout.CancelAfter(ActorHarness.Wait);
        var bytes = await Buffer.DequeueAsync(timeout.Token)
            ?? throw new InvalidOperationException($"The buffer closed with {Buffer.Closed.Result}.");
        var parsed = WireParser.ParseServer(bytes);
        Assert.True(parsed.Ok, $"{parsed.Error}: {parsed.Message}");
        return parsed.Frame!;
    }

    public async Task<T> NextAsync<T>() where T : ServerFrame {
        var frame = await NextAsync();
        return Assert.IsType<T>(frame);
    }

    /// <summary>Skips frames until one of type <typeparamref name="T"/> arrives.</summary>
    public async Task<T> UntilAsync<T>() where T : ServerFrame {
        while (true) {
            if (await NextAsync() is T frame) {
                return frame;
            }
        }
    }

    /// <summary>Skips frames until a tx whose first event is a presence update named with <paramref name="prefix"/>.</summary>
    public async Task<TxFrame> UntilTxAsync(string prefix) {
        while (true) {
            if (await NextAsync() is TxFrame { Ev: [PresenceUpdate update, ..] } tx && update.Name.StartsWith(prefix, StringComparison.Ordinal)) {
                return tx;
            }
        }
    }

    /// <summary>Every frame queued so far.</summary>
    public async Task<List<ServerFrame>> DrainAsync() {
        var frames = new List<ServerFrame>();
        while (Buffer.PendingBytes > 0) {
            frames.Add(await NextAsync());
        }
        return frames;
    }

    public void AssertIdle() => Assert.Equal(0, Buffer.PendingBytes);
}

/// <summary>A store kept in memory: the "database" is a clone of the state that committed events are applied to.</summary>
internal sealed class MemoryStore<TState, TEvent>(TState? persisted, Func<TState, TState> clone, Action<TState, TEvent> apply)
    : IRoomStore<TState, TEvent> where TState : class {
    private readonly Lock _lock = new();
    private readonly Dictionary<ClientId, (PrincipalId Principal, long LastQ)> _cursors = [];
    private readonly List<IReadOnlyList<TEvent>> _commits = [];
    private TState? _persisted = persisted;
    private Func<Task>? _gate;
    private CommitFault? _fault;
    private Exception? _loadFault;
    private Exception? _cursorFault;
    private int _loads;

    public int Loads {
        get {
            lock (_lock) {
                return _loads;
            }
        }
    }

    public IReadOnlyList<IReadOnlyList<TEvent>> Commits {
        get {
            lock (_lock) {
                return [.. _commits];
            }
        }
    }

    public TState? Persisted {
        get {
            lock (_lock) {
                return _persisted is null ? null : clone(_persisted);
            }
        }
    }

    public void SetCursor(ClientId client, PrincipalId principal, long lastQ) {
        lock (_lock) {
            _cursors[client] = (principal, lastQ);
        }
    }

    public long? CursorOf(ClientId client) {
        lock (_lock) {
            return _cursors.TryGetValue(client, out var cursor) ? cursor.LastQ : null;
        }
    }

    /// <summary>The next commit waits for the returned source before it proceeds.</summary>
    public TaskCompletionSource HoldNextCommit() {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock) {
            _gate = async () => {
                entered.TrySetResult();
                await release.Task;
            };
        }
        CommitEntered = entered.Task;
        return release;
    }

    public Task CommitEntered { get; private set; } = Task.CompletedTask;

    /// <summary>The next commit throws <paramref name="exception"/>; with <paramref name="landed"/> it commits first.</summary>
    public void FailNextCommit(Exception exception, bool landed = false) {
        lock (_lock) {
            _fault = new CommitFault(exception, landed);
        }
    }

    /// <summary>The room's rows are deleted (a retention purge).</summary>
    public void Erase() {
        lock (_lock) {
            _persisted = null;
        }
    }

    public void FailLoads(Exception? exception) {
        lock (_lock) {
            _loadFault = exception;
        }
    }

    public Task<TState?> LoadAsync(RoomKey room, DateTimeOffset now, CancellationToken cancellationToken) {
        lock (_lock) {
            _loads++;
            if (_loadFault is { } fault) {
                throw fault;
            }
            return Task.FromResult(_persisted is null ? null : clone(_persisted));
        }
    }

    public void FailCursorLoads(Exception? exception) {
        lock (_lock) {
            _cursorFault = exception;
        }
    }

    public Task<ClientCursor> LoadCursorAsync(RoomKey room, ClientId client, PrincipalId principal, CancellationToken cancellationToken) {
        lock (_lock) {
            if (_cursorFault is { } fault) {
                throw fault;
            }
            if (!_cursors.TryGetValue(client, out var cursor)) {
                return Task.FromResult(new ClientCursor(CursorOwner.None, 0));
            }
            return Task.FromResult(cursor.Principal == principal
                ? new ClientCursor(CursorOwner.Caller, cursor.LastQ)
                : new ClientCursor(CursorOwner.OtherPrincipal, 0));
        }
    }

    public async Task CommitAsync(
        RoomKey room,
        TState state,
        IReadOnlyList<TEvent> events,
        CursorAdvance? cursor,
        CancellationToken cancellationToken) {
        Func<Task>? gate;
        lock (_lock) {
            gate = _gate;
            _gate = null;
        }
        if (gate is not null) {
            await gate();
        }
        lock (_lock) {
            var fault = _fault;
            _fault = null;
            if (fault is null || fault.Landed) {
                foreach (var evt in events) {
                    apply(_persisted!, evt);
                }
                if (cursor is { } advance) {
                    var last = _cursors.TryGetValue(advance.Client, out var existing) ? existing.LastQ : 0;
                    _cursors[advance.Client] = (advance.Principal, Math.Max(last, advance.Q));
                }
                _commits.Add(events);
            }
            if (fault is not null) {
                throw fault.Exception;
            }
        }
    }

    private sealed record CommitFault(Exception Exception, bool Landed);
}

/// <summary>Roles and grants for the tests; nobody has any unless a test grants it.</summary>
internal sealed class TestAccess : IAccessView {
    public Dictionary<(TeamId, PrincipalId), TeamRole> Roles { get; } = [];

    public TeamRole? RoleIn(TeamId team, PrincipalId principal) => Roles.TryGetValue((team, principal), out var role) ? role : null;

    public bool HasCurrentGrant(BoardId board, PrincipalId principal, DateTimeOffset? passwordSetAt) => false;
}

// A counter room: the smallest domain that exercises every runtime path. Poker command types carry the client
// commands, because the runtime only accepts catalog kinds.

internal sealed class Counter {
    public long Value { get; set; }

    public bool Deleted { get; set; }

    public DateTimeOffset? WarmUntil { get; set; }

    public HashSet<PrincipalId> Banned { get; } = [];

    public HashSet<PrincipalId> Members { get; } = [];

    public HashSet<PrincipalId> Mine { get; } = [];

    public Dictionary<string, (DateTimeOffset DueAt, CounterInput Fire)> Timers { get; } = [];

    public CounterInput? OnShutdown { get; set; }

    public Counter Clone() {
        var clone = new Counter { Value = Value, Deleted = Deleted, WarmUntil = WarmUntil, OnShutdown = OnShutdown };
        clone.Banned.UnionWith(Banned);
        clone.Members.UnionWith(Members);
        clone.Mine.UnionWith(Mine);
        foreach (var (key, timer) in Timers) {
            clone.Timers[key] = timer;
        }
        return clone;
    }
}

internal abstract record CounterInput {
    public sealed record Client(Command Command) : CounterInput;

    public sealed record Add(long Amount, EventDurability Durability = EventDurability.Committed) : CounterInput;

    public sealed record SetTimer(string Key, DateTimeOffset DueAt, CounterInput Fire) : CounterInput;

    public sealed record ClearTimer(string Key) : CounterInput;

    public sealed record Ban(PrincipalId Principal) : CounterInput;

    public sealed record Join(PrincipalId Principal) : CounterInput;

    public sealed record Delete : CounterInput;

    /// <summary>Throws from <c>Decide</c>, as a bug would.</summary>
    public sealed record Explode : CounterInput;
}

internal sealed record CounterEvent(string Name, long Amount, EventDurability Durability, PrincipalId? By = null) {
    public string? TimerKey { get; init; }

    public DateTimeOffset DueAt { get; init; }

    public CounterInput? Fire { get; init; }

    public PrincipalId? Subject { get; init; }
}

internal sealed class CounterDomain : IRoomDomain<Counter, CounterInput, CounterEvent> {
    private readonly Lock _lock = new();
    private readonly List<PresenceDelta> _deltas = [];
    private int _warms;

    public int Warms {
        get {
            lock (_lock) {
                return _warms;
            }
        }
    }

    public IReadOnlyList<PresenceDelta> Deltas {
        get {
            lock (_lock) {
                return [.. _deltas];
            }
        }
    }

    public Func<PresenceDelta, IReadOnlyList<CounterEvent>>? PresenceReaction { get; set; }

    public Decision<CounterEvent> Decide(Counter state, CounterInput command, CommandContext context) {
        var by = context.Viewer?.Who.Id;
        return command switch {
            CounterInput.Client { Command: PokerVoteSet vote } => Accept(new CounterEvent("add", vote.Points, EventDurability.Committed, by)),
            CounterInput.Client { Command: PokerLockSet { Locked: true } } => Accept(new CounterEvent("eph", 1, EventDurability.Ephemeral, by)),
            CounterInput.Client { Command: PokerLockSet { Locked: false } } => Accept(new CounterEvent("mem", 1, EventDurability.MemoryOnly, by)),
            CounterInput.Client { Command: PokerSpotlightMove } => Accept(new CounterEvent("move", 0, EventDurability.Ephemeral, by)),
            CounterInput.Client { Command: PokerSpotlightRelease } => new Decision<CounterEvent>.NoOp(),
            CounterInput.Client { Command: PokerRoundReveal } => new Decision<CounterEvent>.Reject(
                NackCode.RoundRevealed,
                "already revealed",
                JsonSerializer.SerializeToElement(state.Value, WireJsonContext.Default.Int64)),
            CounterInput.Client => new Decision<CounterEvent>.Reject(NackCode.Invalid),
            CounterInput.Add add => Accept(new CounterEvent("add", add.Amount, add.Durability, by)),
            CounterInput.SetTimer set => Accept(new CounterEvent("timer", 0, EventDurability.MemoryOnly) {
                TimerKey = set.Key,
                DueAt = set.DueAt,
                Fire = set.Fire,
            }),
            CounterInput.ClearTimer clear => Accept(new CounterEvent("untimer", 0, EventDurability.MemoryOnly) { TimerKey = clear.Key }),
            CounterInput.Ban ban => Accept(new CounterEvent("ban", 0, EventDurability.MemoryOnly) { Subject = ban.Principal }),
            CounterInput.Join join => Accept(new CounterEvent("join", 0, EventDurability.MemoryOnly) { Subject = join.Principal }),
            CounterInput.Delete => Accept(new CounterEvent("delete", 0, EventDurability.Committed)),
            CounterInput.Explode => throw new InvalidOperationException("decider bug"),
            _ => new Decision<CounterEvent>.Reject(NackCode.Invalid),
        };
    }

    public void Apply(Counter state, CounterEvent evt) {
        switch (evt.Name) {
            case "add" or "eph" or "mem":
                state.Value += evt.Amount;
                if (evt.By is { } by) {
                    state.Mine.Add(by);
                }
                break;
            case "timer":
                state.Timers[evt.TimerKey!] = (evt.DueAt, evt.Fire!);
                break;
            case "untimer":
                state.Timers.Remove(evt.TimerKey!);
                break;
            case "ban":
                state.Banned.Add(evt.Subject!.Value);
                break;
            case "join":
                state.Members.Add(evt.Subject!.Value);
                break;
            case "delete":
                state.Deleted = true;
                break;
        }
    }

    public IReadOnlyList<CounterEvent> OnPresence(Counter state, PresenceDelta delta, PresenceView presence, DateTimeOffset now) {
        lock (_lock) {
            _deltas.Add(delta);
        }
        return PresenceReaction?.Invoke(delta) ?? [];
    }

    public IReadOnlyList<TimerRequest<CounterInput>> Timers(Counter state, PresenceView presence, DateTimeOffset now) {
        return [.. state.Timers.Select(x => new TimerRequest<CounterInput>(x.Key, x.Value.DueAt, x.Value.Fire))];
    }

    public IReadOnlyList<CounterEvent> OnWarm(Counter state, PresenceView presence, DateTimeOffset now) {
        lock (_lock) {
            _warms++;
        }
        return [];
    }

    private static Decision<CounterEvent> Accept(params CounterEvent[] events) => new Decision<CounterEvent>.Accept(events);
}

internal sealed class CounterBinding(CounterDomain domain) : IRoomBinding<Counter, CounterInput, CounterEvent> {
    public RoomKind Kind => RoomKind.Poker;

    public IRoomDomain<Counter, CounterInput, CounterEvent> Domain => domain;

    public bool TracksPresence { get; init; } = true;

    public Func<Counter, RoomSnapshot>? SlowSnapshot { get; init; }

    public CounterInput? FromClient(Command command) => command is PokerCommand ? new CounterInput.Client(command) : null;

    public EventDurability DurabilityOf(CounterEvent evt) => evt.Durability;

    public bool CanSubscribe(Counter state, Viewer viewer, IAccessView access) => !state.Banned.Contains(viewer.Who.Id);

    public bool IsMember(Counter state, Viewer viewer, IAccessView access) => state.Members.Contains(viewer.Who.Id);

    public RoomSnapshot Snapshot(Counter state, IReadOnlyList<PrincipalInfo> presence, bool forMembers) {
        if (SlowSnapshot is { } slow) {
            return slow(state);
        }
        return new PokerSnapshot(
            new SessionId(ActorHarness.Room.Id),
            $"v{state.Value}{(forMembers ? "-members" : "")}",
            true,
            true,
            [],
            [],
            [],
            [],
            presence);
    }

    public RoomOverlay? Overlay(Counter state, Viewer viewer) => new PokerOverlay([], state.Mine.Contains(viewer.Who.Id));

    public IReadOnlyList<WireEvent> Project(Counter state, IReadOnlyList<CounterEvent> events, PrincipalId viewer, bool viewerIsMember) {
        return [.. events.Select(evt => new PresenceUpdate(
            viewer,
            $"{evt.Name}:{evt.Amount}:{state.Value}{(evt.By == viewer ? ":mine" : "")}{(viewerIsMember ? ":member" : "")}"))];
    }

    public bool Supersedes(IReadOnlyList<CounterEvent> latest, IReadOnlyList<CounterEvent> previous) {
        return latest.All(x => x.Name == "move") && previous.All(x => x.Name == "move");
    }

    public DateTimeOffset? WarmUntil(Counter state) => state.WarmUntil;

    public bool IsDeleted(Counter state) => state.Deleted;

    public CounterInput? OnShutdown(Counter state) => state.OnShutdown;
}

/// <summary>One counter room: a fake clock, a memory store and an actor (or a registry of them).</summary>
internal sealed class CounterRoom {
    public CounterRoom(Counter? state = null, ActorOptions? options = null, bool tracksPresence = true, Func<Counter, RoomSnapshot>? slowSnapshot = null) {
        Time = ActorHarness.Clock();
        Runtime = ActorHarness.Runtime(Time, options);
        Domain = new CounterDomain();
        Binding = new CounterBinding(Domain) { TracksPresence = tracksPresence, SlowSnapshot = slowSnapshot };
        Store = new MemoryStore<Counter, CounterEvent>(state ?? new Counter(), x => x.Clone(), Domain.Apply);
        Registry = new ActorRegistry(Runtime, [RoomType.Of(Binding, Store, Access)]);
    }

    public FakeTimeProvider Time { get; }

    public ActorRuntime Runtime { get; }

    public CounterDomain Domain { get; }

    public CounterBinding Binding { get; }

    public MemoryStore<Counter, CounterEvent> Store { get; }

    public TestAccess Access { get; } = new();

    public ActorRegistry Registry { get; }

    public async Task<IRoomActor<Counter, CounterInput>> ActorAsync(RoomKey? room = null) {
        return (IRoomActor<Counter, CounterInput>)await Registry.GetOrStartAsync(room ?? ActorHarness.Room, ActorHarness.Token);
    }

    /// <summary>Subscribes a new peer and returns it with its snapshot once the subscription was fully handled.</summary>
    public async Task<(TestPeer Peer, SnapFrame Snap)> JoinAsync(Viewer viewer) {
        var peer = new TestPeer(viewer);
        Assert.Equal(PostResult.Accepted, (await ActorAsync()).Subscribe(peer, viewer));
        var snap = await peer.NextAsync<SnapFrame>();
        await SettleAsync();
        return (peer, snap);
    }

    public Task<T> QueryAsync<T>(Func<RoomView<Counter>, T> query) {
        return Registry.QueryAsync<Counter, CounterInput, T>(ActorHarness.Room, view => ValueTask.FromResult(query(view)), ActorHarness.Token);
    }

    /// <summary>Waits until the actor has handled everything queued on its internal queue.</summary>
    public Task SettleAsync() => QueryAsync(_ => 0);
}
