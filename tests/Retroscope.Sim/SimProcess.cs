using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Core.Team;
using Retroscope.Server.Realtime.Actors;
using Retroscope.Server.Realtime.Transport;

namespace Retroscope.Sim;

/// <summary>
/// One server process: its own clock view, actor runtime and registry over the shared <see cref="SimDb"/>. A crash
/// kills the clock (no timer of this process fires again) and every socket; the next process starts cold.
/// </summary>
internal sealed class SimProcess {
    private static readonly TimeSpan DeadlockAfter = TimeSpan.FromSeconds(20);

    /// <summary>Parked must hold across several looks a yield apart: a wake-up travels through the thread pool.</summary>
    private const int StableChecks = 3;

    private readonly World _world;
    private readonly TaskCompletionSource _crash = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public SimProcess(World world) {
        _world = world;
        Clock = world.Clock.ForProcess();
        var runtime = new ActorRuntime(world.Options, Clock, NullLoggerFactory.Instance);
        RoomType[] types = [
            RoomType.Of(SimPokerBinding.Create(world.Oracle, world.Bug, () => !Dead), new PokerSimStore(world, this), world.Access),
            RoomType.Of(new TeamBinding(world.Oracle, Clock, () => !Dead), new TeamSimStore(world, this), world.Access),
            RoomType.Of(new SimRetroBinding(world.Oracle, () => !Dead), new RetroSimStore(world, this), world.Access),
        ];
        Registry = new ActorRegistry(runtime, types);
        Gateway = new RoomGateway(Registry, types, [], NullLogger<RoomGateway>.Instance);
    }

    public SimClock.ProcessClock Clock { get; }

    public ActorRegistry Registry { get; }

    /// <summary>The real gateway the transport hands every socket's traffic to.</summary>
    public RoomGateway Gateway { get; }

    // Written on actor threads and read by the world, which no longer synchronizes with the actor through a reply.
    private volatile bool _dead;
    private volatile bool _fencingLost;

    public bool Dead => _dead;

    /// <summary>A commit hit the fence: the instance must exit and restart (§c.9).</summary>
    public bool FencingLost {
        get => _fencingLost;
        set => _fencingLost = value;
    }

    /// <summary>A commit landed and the process is to die before anything else happens.</summary>
    public bool CrashRequested => _crash.Task.IsCompleted;

    /// <summary>Completes when the process dies on its own (a commit that crashes it).</summary>
    public Task Crashed => _crash.Task;

    /// <summary>Dies at once, from whatever thread: nothing of this process commits, sends or fires after this.</summary>
    public void RequestCrash() {
        // Signalled first, so the world never sees the process dead without knowing it crashed.
        _crash.TrySetResult();
        Kill();
    }

    public void Kill() {
        _dead = true;
        Clock.Kill();
    }

    /// <summary>
    /// Waits until every live actor (except <paramref name="held"/> ones) has empty queues and is parked waiting for
    /// its next message (<see cref="IQuiescent"/>).
    /// </summary>
    public async Task QuiesceAsync(IReadOnlySet<RoomKey> held) {
        var deadline = Stopwatch.StartNew();
        var spins = 0;
        var quietChecks = 0;
        while (!Dead && !CrashRequested) {
            var quiet = true;
            foreach (var room in _world.Rooms) {
                if (!held.Contains(room) && Registry.TryGet(room, out var actor) && !((IQuiescent)actor).IsParked) {
                    quiet = false;
                    break;
                }
            }
            quietChecks = quiet ? quietChecks + 1 : 0;
            if (quietChecks == StableChecks) {
                return;
            }
            if (deadline.Elapsed > DeadlockAfter) {
                throw new TimeoutException("An actor did not go quiet.");
            }
            if (++spins < 64) {
                await Task.Yield();
            } else {
                await Task.Delay(1);
            }
        }
    }
}

/// <summary>
/// A socket as the transport hands it to the gateway (U14's <see cref="IClientConnection"/>). Frames are parsed when
/// sent, checked by the oracle, and queued for the world to deliver in order.
/// </summary>
internal sealed class SimConnection(World world, SimTab tab, SimProcess process, ConnectionId id, ClientId cid) : IClientConnection {
    private readonly Lock _lock = new();
    private readonly Queue<ServerFrame> _inbox = new();

    public ConnectionId Id { get; } = id;

    /// <summary>The cid the socket's hello bound (the transport's 4409 rule looks at it).</summary>
    public ClientId Cid { get; } = cid;

    public SimTab Tab { get; } = tab;

    public SimProcess Process { get; } = process;

    public Viewer Viewer { get; } = new(tab.Who, cid, id);

    public Guid? AuthSessionId => null;

    /// <summary>Rooms this socket asked for, so the transport can tell each actor when the socket closes.</summary>
    public HashSet<RoomKey> Rooms { get; } = [];

    /// <summary>The client end is gone (drop, crash, or the client read <c>bye</c>).</summary>
    public bool ClientClosed { get; private set; }

    /// <summary>The server sent <c>bye</c> and is closing with 1012.</summary>
    public bool ServerClosing { get; private set; }

    public bool Open {
        get {
            lock (_lock) {
                return !ClientClosed && !ServerClosing && !Process.Dead;
            }
        }
    }

    public int Pending {
        get {
            lock (_lock) {
                return _inbox.Count;
            }
        }
    }

    public bool Send(ServerFrame frame) => Send(WireWriter.ToUtf8(frame));

    public bool Send(IReadOnlyList<ReadOnlyMemory<byte>> segments) {
        var bytes = new List<byte>();
        foreach (var segment in segments) {
            bytes.AddRange(segment.ToArray());
        }
        return Send(bytes.ToArray());
    }

    /// <summary>
    /// A socket whose client already vanished still takes writes until the server notices (keep-alive), so the
    /// frame is observed and dropped rather than refused.
    /// </summary>
    public bool Send(ReadOnlyMemory<byte> utf8) {
        lock (_lock) {
            if (ServerClosing || Process.Dead) {
                return false;
            }
        }
        var parsed = WireParser.ParseServer(utf8.Span);
        if (!parsed.Ok) {
            world.Oracle.Fail($"the server sent an unparseable frame: {parsed.Error} {parsed.Message}");
            return true;
        }
        world.Observe(this, parsed.Frame!);
        lock (_lock) {
            if (!ClientClosed) {
                _inbox.Enqueue(parsed.Frame!);
            }
        }
        return true;
    }

    /// <summary>The transport drops the room from the socket and tells the client why.</summary>
    public void Kick(RoomKey room, KickCode code) {
        Rooms.Remove(room);
        Send(new KickFrame(room, code));
    }

    public void Restart() {
        if (world.Draining) {
            return;
        }
        lock (_lock) {
            if (ServerClosing) {
                return;
            }
            ServerClosing = true;
            if (!ClientClosed) {
                // §c.3: retryMs is drawn per connection; a hash keeps the draw out of the world's random stream.
                var retryMs = 500 + (int)((uint)Id.Value.GetHashCode() % 1500);
                _inbox.Enqueue(new ByeFrame(ByeCode.Restart, retryMs));
            }
        }
        world.Restarted(this);
    }

    public ServerFrame? Take() {
        lock (_lock) {
            return _inbox.TryDequeue(out var frame) ? frame : null;
        }
    }

    /// <summary>The socket died under the client: frames not yet read are lost.</summary>
    public void CloseClient() {
        lock (_lock) {
            ClientClosed = true;
            _inbox.Clear();
        }
    }
}
