using System.Text;
using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using Retroscope.Core.Room;
using Retroscope.Core.Team;
using Retroscope.Core.Tests.Retro;
using Retroscope.Server.Realtime.Actors;

namespace Retroscope.Sim;

/// <summary>
/// One seeded run: a team, its poker session and board, five tabs of four people, and a server process that crashes,
/// redeploys and loses commits while the tabs vote, drop off the network and lose their storage. Everything that
/// happens is drawn from one <see cref="Random"/>, and the world waits for every actor to go quiet after each
/// message, so a seed replays exactly.
/// </summary>
internal sealed class World {
    public static readonly DateTimeOffset Start = new(2026, 10, 21, 9, 0, 0, TimeSpan.Zero);

    private static readonly TimeSpan HealLimit = TimeSpan.FromMinutes(5);

    private readonly int _seed;
    private readonly WorldOptions _options;
    private readonly List<SimTab> _tabs = [];
    private readonly HashSet<RoomKey> _held = [];
    private readonly HashSet<SimConnection> _serverLive = [];
    private readonly Queue<SimConnection> _restarts = new();
    private readonly Queue<(SimProcess Process, Action<ActorRegistry> Post)> _posts = new();
    private readonly Lock _lock = new();
    private readonly List<string> _trace = [];
    private readonly Dictionary<SimTab, Storage> _checkpoints = [];
    private readonly Stats _stats = new();
    private readonly Principal _alice;
    private readonly Principal _bob;
    private readonly Principal _carol;
    private readonly Principal _dave;
    private readonly Dictionary<Guid, (PrincipalId Author, bool Anonymous)> _authors = [];
    private RoundId? _shownSpotlight;
    private long _ids;
    private int _intents;
    private int _tabNames;

    public World(int seed, WorldOptions options) {
        _seed = seed;
        _options = options;
        Rng = new Random(seed);
        Clock = new SimClock(Start);
        Oracle = new Oracle(Clock);
        if (options.Trace) {
            Oracle.Trace = Trace;
        }
        TeamRoom = RoomKey.Team(new TeamId(NewGuid()));
        PokerRoom = RoomKey.Poker(new SessionId(NewGuid()));
        BoardRoom = RoomKey.Board(new BoardId(NewGuid()));
        Rooms = [PokerRoom, TeamRoom, BoardRoom];
        Access = new SimAccess(new TeamId(TeamRoom.Id));
        _alice = new Principal(new PrincipalId(NewGuid()), PrincipalKind.User, "Alice", null, false);
        _bob = new Principal(new PrincipalId(NewGuid()), PrincipalKind.User, "Bob", null, false);
        _carol = new Principal(new PrincipalId(NewGuid()), PrincipalKind.User, "Carol", null, false);
        _dave = new Principal(new PrincipalId(NewGuid()), PrincipalKind.User, "Dave", null, false);
        Options = new ActorOptions {
            ExternalQueueCapacity = 6,
            RingBytesPerRoom = Rng.Next(2) == 0 ? 4 * 1024 : 256 * 1024,
        };
        Db = CreateDb();
        Access.Set(Db.Members);
        Process = new SimProcess(this);
    }

    public Random Rng { get; }

    public SimClock Clock { get; }

    public Oracle Oracle { get; }

    public SimDb Db { get; }

    public SimAccess Access { get; }

    public ActorOptions Options { get; }

    public InjectedBug Bug => _options.Bug;

    public SimProcess Process { get; private set; }

    public RoomKey PokerRoom { get; }

    public RoomKey TeamRoom { get; }

    public RoomKey BoardRoom { get; }

    public IReadOnlyList<RoomKey> Rooms { get; }

    /// <summary>A graceful shutdown is draining the actors; sockets are closed afterwards, not by each actor.</summary>
    public bool Draining { get; private set; }

    private bool Has(Faults fault) => (_options.Faults & fault) == fault;

    public async Task<WorldResult> RunAsync() {
        try {
            foreach (var (name, who) in new[] { ("alice", _alice), ("bob", _bob), ("bob-2", _bob), ("carol", _carol), ("dave", _dave) }) {
                var tab = new SimTab(this, name, who, new ClientId(NewGuid()), Rooms);
                _tabs.Add(tab);
                await tab.ConnectAsync();
            }
            for (var step = 0; step < _options.Steps && !Oracle.Failed; step++) {
                Oracle.Step = step;
                await StepAsync();
                await SettleAsync();
            }
            if (!Oracle.Failed) {
                Oracle.Step = _options.Steps;
                await HealAsync();
                await CheckAsync();
            }
        } catch (TimeoutException) {
            Oracle.Fail("deadlock: an actor did not answer within 20 s of real time");
        } catch (Exception e) {
            Oracle.Fail($"harness or runtime exception: {e}");
        }
        foreach (var intent in Oracle.Intents) {
            switch (intent.Outcome) {
                case IntentOutcome.Acked:
                    _stats.Acked++;
                    break;
                case IntentOutcome.Nacked:
                    _stats.Nacked++;
                    break;
                case IntentOutcome.Dropped:
                    _stats.Dropped++;
                    break;
                case IntentOutcome.Lost:
                    _stats.Lost++;
                    break;
            }
        }
        _stats.Commits = Oracle.Commits;
        _stats.AutoReveals = Oracle.AutoReveals;
        _stats.TimerExpiries = Oracle.TimerExpiries;
        _stats.TwinResiduals = Oracle.TwinResiduals;
        return new WorldResult(_seed, Digest(), Oracle.Violations, Oracle.Known, _trace, _stats);
    }

    public Guid NewGuid() {
        var id = ++_ids;
        return new Guid((uint)_seed, (ushort)(id >> 32), (ushort)(0x4000 | ((id >> 16) & 0x0fff)), 0x80, 0, 0, 0, 0, (byte)(id >> 8), (byte)id, 0);
    }

    public Intent NewIntent(RoomKey room, Command command, PrincipalId by) {
        var intent = new Intent(++_intents, room, command, Clock.Now, Catalog.EntryOf(command).Ephemeral);
        switch (command) {
            case RetroCardAdd add:
                lock (_lock) {
                    _authors[add.CardId.Value] = (by, add.Anonymous);
                }
                break;
            case RetroCommentAdd add:
                lock (_lock) {
                    _authors[add.CommentId.Value] = (by, add.Anonymous);
                }
                break;
        }
        var parsed = WireParser.ParseClient(WireWriter.ToUtf8(new CmdFrame(room, 1, command)));
        intent.DataHash = parsed.Ok ? ((CmdFrame)parsed.Frame!).DataHash : 0;
        Oracle.Register(intent);
        _stats.Commands++;
        return intent;
    }

    /// <summary>The command edits or deletes a card or comment <paramref name="who"/> posted while still on the team.</summary>
    public bool Authored(Command command, PrincipalId who) {
        Guid? post = command switch {
            RetroCardEdit edit => edit.CardId.Value,
            RetroCardDelete delete => delete.CardId.Value,
            RetroCommentDelete delete => delete.CommentId.Value,
            _ => null,
        };
        lock (_lock) {
            return post is { } id && _authors.TryGetValue(id, out var author) && author.Author == who && Db.Members.Any(x => x.Pid == who);
        }
    }

    /// <summary>The cards and comments <paramref name="who"/> posted, for the workload's edits and deletes.</summary>
    public bool IsAuthor(Guid post, PrincipalId who) {
        lock (_lock) {
            return _authors.TryGetValue(post, out var author) && author.Author == who;
        }
    }

    public void Trace(string line) {
        if (_options.Trace) {
            _trace.Add($"[{Oracle.Step,4}] t+{(Clock.Now - Start).TotalMilliseconds,8:0}ms {line}");
        }
    }

    /// <summary>Hands one frame to the gateway and waits until every actor is quiet again.</summary>
    public async Task SendAsync(Action send) {
        send();
        await SettleAsync();
    }

    public SimConnection Open(SimTab tab, ConnectionId id, ClientId cid) {
        var connection = new SimConnection(this, tab, Process, id, cid);
        _serverLive.Add(connection);
        return connection;
    }

    /// <summary>The transport's 4409 rule: the cid is bound to another socket the server still holds.</summary>
    public bool CidHeldElsewhere(SimTab tab) {
        return _serverLive.Any(x => x.Cid == tab.Cid && x != tab.Connection && !x.Process.Dead);
    }

    /// <summary>Another open tab answers BroadcastChannel with the same cid (U28 decision 7).</summary>
    public bool TwinAnswers(SimTab tab) => _tabs.Any(x => x != tab && !x.Closed && x.Cid == tab.Cid);

    /// <summary>The transport tells every room the socket was in that it closed.</summary>
    public async Task ServerNoticesCloseAsync(SimConnection connection) {
        if (!_serverLive.Remove(connection) || connection.Process.Dead) {
            return;
        }
        foreach (var room in connection.Rooms.ToList()) {
            Oracle.Disconnected(room, connection.Id);
            connection.Process.Gateway.Disconnected(connection, [room]);
            await SettleAsync();
        }
    }

    /// <summary>
    /// An actor-to-actor post (§c.2), made on the posting actor's thread and enqueued once every actor is quiet.
    /// Both are non-blocking enqueues; deferring it fixes the interleaving of the two actors (the poster finishes
    /// its message first), so a seed replays exactly.
    /// </summary>
    public void PostLater(SimProcess process, Action<ActorRegistry> post) {
        lock (_lock) {
            _posts.Enqueue((process, post));
        }
    }

    /// <summary>Called on an actor thread by <see cref="SimConnection.Restart"/>; handled once the actors are quiet.</summary>
    public void Restarted(SimConnection connection) {
        lock (_lock) {
            _restarts.Enqueue(connection);
        }
    }

    /// <summary>The oracle's view of every frame the server sends (actor thread).</summary>
    public void Observe(SimConnection connection, ServerFrame frame) {
        switch (frame) {
            case SnapFrame snap:
                Oracle.Connected(snap.R, connection.Id, connection.Tab.Who.Id);
                if (snap is { D: RetroSnapshot board, O: RetroOverlay overlay }) {
                    CheckAnonymity(connection.Tab, board.Cards, board.Comments);
                    CheckMine(connection.Tab, [.. overlay.MineCards.Select(x => x.Value), .. overlay.MineComments.Select(x => x.Value)]);
                }
                break;
            case ReplayFrame replay:
                Oracle.Connected(replay.R, connection.Id, connection.Tab.Who.Id);
                break;
            case KickFrame kick:
                Oracle.Disconnected(kick.R, connection.Id);
                break;
            case NackFrame { Code: NackCode.CidReuse }:
                lock (_lock) {
                    _stats.CidReuseNacks++;
                }
                break;
            case TxFrame tx:
                foreach (var evt in tx.Ev) {
                    switch (evt) {
                        case PresenceLeave leave:
                            Oracle.PresenceLeft(tx.R, leave.Pid);
                            break;
                        case PokerSpotlight spotlight:
                            lock (_lock) {
                                _shownSpotlight = spotlight.RoundId;
                            }
                            break;
                        case PokerVoteState { Points: not null } vote when vote.Pid != connection.Tab.Who.Id:
                            Oracle.Fail($"secrecy: {connection.Tab} was sent {vote.Pid}'s points while the round is voting");
                            break;
                        case RetroCardAdded added:
                            CheckAnonymity(connection.Tab, [added.Card], []);
                            break;
                        case RetroCommentAdded added:
                            CheckAnonymity(connection.Tab, [], [added.Comment]);
                            break;
                    }
                }
                break;
        }
    }

    /// <summary>
    /// S6 on the wire: an anonymous post never names its author, and only its author's tabs are told it is theirs
    /// (the board knows them by the anonymous tag alone).
    /// </summary>
    private void CheckAnonymity(SimTab tab, IReadOnlyList<CardInfo> cards, IReadOnlyList<CommentInfo> comments) {
        var posts = cards.Select(x => (x.Id.Value, x.Anonymous, x.Author, x.Mine))
            .Concat(comments.Select(x => (x.Id.Value, x.Anonymous, x.Author, x.Mine)));
        foreach (var (id, anonymous, author, mine) in posts) {
            (PrincipalId Author, bool Anonymous) posted;
            lock (_lock) {
                if (!_authors.TryGetValue(id, out posted)) {
                    continue;
                }
            }
            if (posted.Anonymous && (!anonymous || author is not null)) {
                Oracle.Fail($"anonymity: {tab} was sent anonymous post {id} with its author ({author?.Name ?? "not marked anonymous"})");
            }
            if (mine == true) {
                CheckMine(tab, [id]);
            }
        }
    }

    private void CheckMine(SimTab tab, IEnumerable<Guid> mine) {
        foreach (var id in mine) {
            lock (_lock) {
                if (_authors.TryGetValue(id, out var posted) && posted.Author != tab.Who.Id) {
                    Oracle.Fail($"anonymity: {tab} was told post {id} by someone else is its own");
                }
            }
        }
    }

    public void CheckSnapshotSecrecy(SimTab tab, PokerSnapshot snapshot) {
        var voting = snapshot.Rounds.Where(x => x.State == RoundState.Voting).Select(x => x.Id).ToHashSet();
        if (snapshot.Votes.Any(x => voting.Contains(x.RoundId) && x.Points is not null)) {
            Oracle.Fail($"secrecy: {tab}'s snapshot carries points of a voting round");
        }
    }

    /// <summary>Waits for quiet, then handles what quiet revealed: restarted sockets, a dying or fenced process.</summary>
    public async Task SettleAsync() {
        while (true) {
            await Process.QuiesceAsync(_held);
            if (Process.CrashRequested) {
                Crash();
                return;
            }
            if (Process.FencingLost) {
                Fenced();
                return;
            }
            SimConnection? restarted;
            (SimProcess Process, Action<ActorRegistry> Post) posted;
            bool hasPost;
            lock (_lock) {
                _restarts.TryDequeue(out restarted);
                hasPost = _posts.TryDequeue(out posted);
            }
            if (restarted is null && !hasPost) {
                return;
            }
            if (restarted is not null) {
                await ServerNoticesCloseAsync(restarted);
            }
            if (hasPost && posted.Process == Process && !Process.Dead) {
                posted.Post(Process.Registry);
            }
        }
    }

    private async Task StepAsync() {
        var roll = Rng.Next(1000);
        switch (roll) {
            case < 380:
                await CommandAsync(PickTab());
                break;
            case < 600:
                await DeliverAsync(PickTab(), Rng.Next(1, 6));
                break;
            case < 760:
                await AdvanceAsync(RandomDelay());
                break;
            case < 790 when Has(Faults.Network):
                await DropAsync(PickTab());
                break;
            case < 815 when Has(Faults.Commits):
                ArmCommitFault();
                break;
            case < 820 when Has(Faults.Crashes):
                Trace("kill -9");
                Process.RequestCrash();
                break;
            case < 825 when Has(Faults.Crashes):
                await RedeployAsync();
                break;
            case < 840 when Has(Faults.Storage):
                await StorageFaultAsync(PickTab());
                break;
            case < 852 when Has(Faults.Membership):
                await MembershipAsync();
                break;
            case < 870 when Has(Faults.Board):
                await BoardAsync();
                break;
            case < 876 when Has(Faults.FullQueues):
                await FullQueuesAsync();
                break;
            case < 890:
                await RejoinAsync(PickTab());
                break;
            case < 915:
                await VotingBurstAsync();
                break;
            default:
                await DeliverAllAsync();
                break;
        }
        if (_options.Faults.HasFlag(Faults.StaleStorage) && Oracle.Step % 25 == 0) {
            foreach (var tab in _tabs) {
                _checkpoints[tab] = tab.Save();
            }
        }
    }

    private SimTab PickTab() {
        var open = _tabs.Where(x => !x.Closed).ToList();
        return open[Rng.Next(open.Count)];
    }

    private TimeSpan RandomDelay() {
        return Rng.Next(100) switch {
            < 60 => TimeSpan.FromMilliseconds(Rng.Next(10, 300)),
            < 85 => TimeSpan.FromMilliseconds(Rng.Next(300, 3000)),
            < 97 => TimeSpan.FromMilliseconds(Rng.Next(3000, 20_000)),
            _ => TimeSpan.FromSeconds(Rng.Next(60, 240)),
        };
    }

    private async Task CommandAsync(SimTab tab) {
        var key = Rng.Next(100) switch {
            < 60 => PokerRoom,
            < 85 => BoardRoom,
            _ => TeamRoom,
        };
        var room = tab.Room(key);
        var command = key == PokerRoom ? Workload.Poker(this, tab, room)
            : key == BoardRoom ? Workload.Retro(this, tab, room)
            : Workload.Team(this, tab, room);
        if (command is not null) {
            await tab.IssueAsync(key, command);
        }
    }

    /// <summary>Everyone votes and locks on the round they look at (the soak's burst, §j): auto-reveal territory.</summary>
    private async Task VotingBurstAsync() {
        Trace("voting burst");
        foreach (var tab in _tabs.Where(x => !x.Closed).ToList()) {
            if (tab.Room(PokerRoom).Poker?.Viewed is { } round) {
                await tab.IssueAsync(PokerRoom, new PokerVoteSet(round, Deck.Values[Rng.Next(Deck.Values.Length)], Lock: true));
            }
        }
    }

    /// <summary>Every frame in flight, to every tab; true when there was any.</summary>
    private async Task<bool> DeliverAllAsync() {
        var delivered = false;
        foreach (var tab in _tabs.ToList()) {
            while (tab.Connection is { ClientClosed: false } connection && connection.Take() is { } frame) {
                await tab.HandleAsync(frame);
                delivered = true;
            }
        }
        return delivered;
    }

    private static async Task DeliverAsync(SimTab tab, int count) {
        for (var i = 0; i < count; i++) {
            if (tab.Connection is not { ClientClosed: false } connection || connection.Take() is not { } frame) {
                return;
            }
            await tab.HandleAsync(frame);
        }
    }

    /// <summary>
    /// Moves the clock, firing each due timer at its own time and waiting for quiet after each. Frames in flight
    /// arrive first: no network holds a frame for longer than the step (latency is at most an instant here, so the
    /// window between a frame and the next client action is the same-instant interleaving of steps).
    /// </summary>
    private async Task AdvanceAsync(TimeSpan by) {
        await DeliverAllAsync();
        var until = Clock.Now + by;
        while (Clock.TakeDue(until) is { } timer) {
            await timer.FireAsync();
            await SettleAsync();
        }
        Clock.MoveTo(until);
    }

    private async Task DropAsync(SimTab tab) {
        if (tab.Connection is not { ClientClosed: false } connection) {
            return;
        }
        _stats.Drops++;
        await DeliverAsync(tab, Rng.Next(0, 3));
        Trace($"{tab}: network drop");
        tab.Dropped();
        if (Has(Faults.StaleSocket) && Rng.Next(3) == 0) {
            // The server only notices when its keep-alive gives up (§c.3: about 45 s).
            Clock.Schedule(Clock.Now + TimeSpan.FromSeconds(45), () => ServerNoticesCloseAsync(connection));
            return;
        }
        await ServerNoticesCloseAsync(connection);
    }

    private void ArmCommitFault() {
        var room = Rng.Next(100) switch {
            < 55 => PokerRoom,
            < 80 => BoardRoom,
            _ => TeamRoom,
        };
        var fault = Rng.Next(100) switch {
            < 25 => CommitFault.FailedBeforeCommit,
            < 55 => CommitFault.OutcomeUnknownLanded,
            < 65 => CommitFault.OutcomeUnknownLost,
            < 72 => CommitFault.Rejected,
            < 82 => CommitFault.FencingLost,
            _ => Has(Faults.Crashes) ? CommitFault.CrashAfterCommit : CommitFault.OutcomeUnknownLanded,
        };
        _stats.CommitFaults++;
        Trace($"next {room.Kind} commit: {fault}");
        Db.Arm(room, fault);
    }

    /// <summary>kill -9 (or a commit that took the process down): sockets die without a bye, the next process starts cold.</summary>
    private void Crash() {
        _stats.Crashes++;
        CountLostSpotlight();
        Trace("process crashed; starting a new one");
        Process.Kill();
        foreach (var tab in _tabs.Where(x => x.Connection is { ClientClosed: false })) {
            tab.Dropped();
        }
        Replace(Process);
    }

    /// <summary>§c.9: on fencing loss the instance stops its actors, says bye to every socket and exits.</summary>
    private void Fenced() {
        _stats.Crashes++;
        CountLostSpotlight();
        Trace("fencing lost; the instance exits and restarts");
        var old = Process;
        old.Kill();
        foreach (var connection in _serverLive.Where(x => x.Process == old).ToList()) {
            connection.Restart();
        }
        Replace(old);
    }

    /// <summary>Stop-and-recreate (§c.9 graceful shutdown): actors persist the spotlight and send bye.</summary>
    private async Task RedeployAsync() {
        _stats.Redeploys++;
        Trace("redeploy");
        var old = Process;
        // U14 decision 5: actors drain first (DrainActors), then the transport says bye to every socket.
        Draining = true;
        await UnlessCrashedAsync(old, old.Registry.ShutdownAsync(CancellationToken.None));
        Draining = false;
        if (old.CrashRequested || old.FencingLost) {
            await SettleAsync();
            return;
        }
        old.Kill();
        foreach (var connection in _serverLive.Where(x => x.Process == old).ToList()) {
            connection.Restart();
        }
        Replace(old);
    }

    /// <summary>The spotlight clients last saw is not the one in the database: an unsettled move died with the process.</summary>
    private void CountLostSpotlight() {
        lock (_lock) {
            if (_shownSpotlight is { } shown && Db.PokerRows.Spotlight is { } persisted && persisted.RoundId != shown) {
                _stats.SpotlightLost++;
                Trace("an unsettled spotlight position was lost");
            }
            _shownSpotlight = null;
        }
    }

    /// <summary>The old process's sockets are gone with it; a new process starts with no actors loaded.</summary>
    private void Replace(SimProcess old) {
        foreach (var connection in _serverLive.Where(x => x.Process == old)) {
            foreach (var room in connection.Rooms) {
                Oracle.Disconnected(room, connection.Id);
            }
        }
        _serverLive.RemoveWhere(x => x.Process == old);
        _held.Clear();
        Oracle.Restarted();
        lock (_lock) {
            _restarts.Clear();
            _posts.Clear();
        }
        Process = new SimProcess(this);
    }

    private async Task StorageFaultAsync(SimTab tab) {
        _stats.StorageFaults++;
        var roll = Rng.Next(100);
        if (roll < 20) {
            var twin = Has(Faults.Twins) && Rng.Next(2) == 0;
            var copy = tab.Duplicate($"{tab.Name}-copy{++_tabNames}", twin);
            Trace($"{tab}: duplicated into {copy}{(twin ? " (twin, original stays)" : ", original closed")}");
            if (!twin) {
                await tab.CloseForeverAsync();
            }
            _tabs.Add(copy);
            await copy.ConnectAsync();
            return;
        }
        var kind = roll switch {
            < 45 => Reload.Intact,
            < 70 => Reload.RegressedCseq,
            < 85 => Reload.LostOutbox,
            < 95 => Reload.LostStorage,
            _ => Has(Faults.StaleStorage) ? Reload.StaleStorage : Reload.RegressedCseq,
        };
        await tab.ReloadAsync(kind, _checkpoints.GetValueOrDefault(tab));
    }

    /// <summary>HTTP membership changes, routed through the team actor (§c.2, R24).</summary>
    private async Task MembershipAsync() {
        var members = Db.Members;
        TeamInput input;
        if (Rng.Next(3) == 0) {
            input = members.Any(x => x.Pid == _dave.Id)
                ? new TeamInput.MemberRemoved(_dave.Id)
                : new TeamInput.MemberAdded(new MemberInfo(_dave.Id, _dave.DisplayName, TeamRole.Member));
        } else {
            var who = Rng.Next(2) == 0 ? _bob : _carol;
            var role = members.FirstOrDefault(x => x.Pid == who.Id)?.Role == TeamRole.Admin ? TeamRole.Member : TeamRole.Admin;
            input = new TeamInput.MemberRoleSet(who.Id, role);
        }
        Trace($"http: {input}");
        var process = Process;
        await UnlessCrashedAsync(process, process.Registry.AskAsync<TeamState, TeamInput>(TeamRoom, input, null, CancellationToken.None));
        await SettleAsync();
    }

    /// <summary>What a board actor does to the team's action items: create in place, edit, delete (§c.2).</summary>
    private async Task BoardAsync() {
        var cards = Db.Cards.Keys.OrderBy(x => x.Value).ToList();
        var roll = Rng.Next(100);
        TeamInput input;
        if (roll < 45 || cards.Count == 0) {
            var card = new CardId(NewGuid());
            var text = $"action {_ids}";
            var item = new ActionItemInfo(new ActionItemId(NewGuid()), text, 1, Clock.Now.ToUnixTimeMilliseconds(), 0, new BoardId(NewGuid()), card, CreatedBy: _carol.Id);
            Db.InsertActionItem(item, text);
            input = new TeamInput.ActionItemCreated(item);
        } else if (roll < 85) {
            var card = cards[Rng.Next(cards.Count)];
            var version = Db.Cards[card].Version + 1;
            var text = $"edited {_ids++}";
            Db.EditCard(card, text, version);
            input = new TeamInput.SyncActionText(card, text, version);
        } else {
            var card = cards[Rng.Next(cards.Count)];
            Db.DeleteCard(card);
            input = new TeamInput.SourceCardDeleted(card);
        }
        Trace($"board: {input.GetType().Name}");
        PostLater(Process, registry => registry.Post<TeamState, TeamInput>(TeamRoom, input));
        await SettleAsync();
    }

    /// <summary>
    /// Both actors stall on a slow transaction while their bounded queues fill and they keep posting to each other
    /// (R10, R26): the overflow is nacked <c>server_busy</c>, the posts wait on the unbounded internal queues, and
    /// nothing deadlocks once the stalls end.
    /// </summary>
    private async Task FullQueuesAsync() {
        if (!Process.Registry.TryGet(PokerRoom, out _) || !Process.Registry.TryGet(TeamRoom, out _)) {
            return;
        }
        _stats.FullQueueEpisodes++;
        Trace("full queues: both actors stall");
        var poker = await HoldAsync(PokerRoom);
        var team = await HoldAsync(TeamRoom);
        if (poker is null || team is null) {
            return;
        }
        for (var i = 0; i < Options.ExternalQueueCapacity + 3; i++) {
            var tab = PickTab();
            if (Workload.Poker(this, tab, tab.Room(PokerRoom)) is { } pokerCommand) {
                await tab.IssueAsync(PokerRoom, pokerCommand);
            }
            if (Workload.Team(this, tab, tab.Room(TeamRoom)) is { } teamCommand) {
                await tab.IssueAsync(TeamRoom, teamCommand);
            }
        }
        var member = Rng.Next(2) == 0 ? _bob : _carol;
        var process = Process;
        var ask = process.Registry.AskAsync<TeamState, TeamInput>(
            TeamRoom,
            new TeamInput.MemberRoleSet(member.Id, Db.Members.FirstOrDefault(x => x.Pid == member.Id)?.Role == TeamRole.Admin ? TeamRole.Member : TeamRole.Admin),
            null,
            CancellationToken.None);
        Release(PokerRoom, poker);
        await SettleAsync();
        if (Process.Dead) {
            return;
        }
        poker = await HoldAsync(PokerRoom);
        Release(TeamRoom, team);
        await SettleAsync();
        if (poker is not null) {
            Release(PokerRoom, poker);
        }
        await SettleAsync();
        await UnlessCrashedAsync(process, ask);
    }

    /// <summary>Waits for an HTTP-style ask, unless its process dies first (its reply then never comes).</summary>
    private static async Task UnlessCrashedAsync(SimProcess process, Task ask) {
        await Task.WhenAny(ask, process.Crashed).WaitAsync(TimeSpan.FromSeconds(20));
    }

    /// <summary>Parks the actor on a query, as a slow transaction would; null when the room is not live.</summary>
    private async Task<TaskCompletionSource?> HoldAsync(RoomKey room) {
        if (Process.Dead || !Process.Registry.TryGet(room, out var actor)) {
            return null;
        }
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask<int> Stall<TState>(RoomView<TState> view) where TState : class {
            entered.TrySetResult();
            await release.Task;
            return 0;
        }
        _ = actor switch {
            IRoomActor<PokerState, PokerInput> poker => poker.QueryAsync(Stall, CancellationToken.None),
            IRoomActor<TeamState, TeamInput> team => team.QueryAsync(Stall, CancellationToken.None),
            IRoomActor<RetroState, RetroInput> board => board.QueryAsync(Stall, CancellationToken.None),
            _ => throw new InvalidOperationException(),
        };
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(20));
        _held.Add(room);
        return release;
    }

    private void Release(RoomKey room, TaskCompletionSource release) {
        _held.Remove(room);
        release.TrySetResult();
    }

    private static async Task RejoinAsync(SimTab tab) {
        if (tab.Connection is not { Open: true }) {
            return;
        }
        foreach (var room in tab.Rooms.Where(x => x.Kicked is not null).ToList()) {
            await tab.RejoinAsync(room);
        }
    }

    /// <summary>
    /// Faults stop, every tab comes back, and time passes until every outbox is empty and every frame delivered.
    /// </summary>
    private async Task HealAsync() {
        Trace("healing");
        foreach (var room in Rooms) {
            Db.TakeFault(room);
        }
        var deadline = Clock.Now + HealLimit;
        var quiet = 0;
        while (Clock.Now < deadline && quiet < 20) {
            foreach (var tab in _tabs.Where(x => !x.Closed).ToList()) {
                if (tab.Connection is not { ClientClosed: false } && !tab.Reconnecting) {
                    await tab.ConnectAsync();
                }
                if (tab.Connection is { Open: true }) {
                    foreach (var room in tab.Rooms.Where(x => x.Kicked is not null && MayJoin(tab)).ToList()) {
                        await tab.RejoinAsync(room);
                    }
                }
            }
            var delivered = await DeliverAllAsync();
            await AdvanceAsync(TimeSpan.FromSeconds(1));
            delivered |= await DeliverAllAsync();
            // Quiet for longer than the warm-up (15 s) and the presence grace, so no timer is still due.
            quiet = !delivered && Converged() ? quiet + 1 : 0;
        }
        if (!Converged()) {
            var pending = _tabs.Where(x => !x.Closed).SelectMany(x => x.Entries.Select(e => $"{x}: {e.Intent} q={e.Q}"));
            Oracle.Fail($"did not converge within {HealLimit.TotalMinutes:0} min of healing: {string.Join("; ", pending.Take(5))}");
        }
    }

    /// <summary>Every room is for team members only (§c.11; the board is team-visible).</summary>
    private bool MayJoin(SimTab tab) => Db.Members.Any(x => x.Pid == tab.Who.Id);

    private bool Converged() {
        foreach (var tab in _tabs.Where(x => !x.Closed)) {
            if (tab.Connection is not { Open: true } || tab.Entries.Count > 0 || tab.Connection.Pending > 0) {
                return false;
            }
            if (tab.Rooms.Any(x => x.Kicked is null ? !x.Synced || x.Resyncing : MayJoin(tab))) {
                return false;
            }
        }
        return true;
    }

    /// <summary>The end-of-run invariants of §j.</summary>
    private async Task CheckAsync() {
        foreach (var intent in Oracle.Intents.Where(x => x.Outcome == IntentOutcome.Pending)) {
            Oracle.Fail($"{intent} was never answered (issued at t+{(intent.IssuedAt - Start).TotalMilliseconds:0}ms)");
        }
        foreach (var tab in _tabs.Where(x => !x.Closed)) {
            foreach (var room in tab.Rooms.Where(x => x.Kicked is null)) {
                await CompareViewAsync(tab, room);
            }
        }
        Oracle.CheckPresence(Options.PresenceGrace);
        await CheckDurableAsync();
        if (Db.RetroRows().Timer is { State: TimerState.Running, EndsAt: { } end } && end <= Clock.Now) {
            Oracle.Fail($"the board's timer ended at t+{(end - Start).TotalMilliseconds:0}ms and never expired");
        }
    }

    /// <summary>Every client's folded view equals the snapshot the server would send it now.</summary>
    private async Task CompareViewAsync(SimTab tab, RoomClient room) {
        var viewer = tab.Viewer;
        byte[] bytes;
        if (room.Key == PokerRoom) {
            bytes = await QueryAsync<PokerState, PokerInput, byte[]>(PokerRoom, view => view.SnapFrameAsync(viewer));
        } else if (room.Key == BoardRoom) {
            bytes = await QueryAsync<RetroState, RetroInput, byte[]>(BoardRoom, view => view.SnapFrameAsync(viewer));
        } else {
            bytes = await QueryAsync<TeamState, TeamInput, byte[]>(TeamRoom, view => view.SnapFrameAsync(viewer));
        }
        var expected = (SnapFrame)WireParser.ParseServer(bytes).Frame!;
        if (room.Retro is { } retro) {
            var snapshot = (RetroSnapshot)expected.D;
            var folded = retro.ToJson() + "\n" + Json(room.Presence.OrderBy(x => x.Pid.ToString(), StringComparer.Ordinal));
            var fresh = RetroViewerModel.Canonical(snapshot with { Presence = [] }, (RetroOverlay)expected.O!) + "\n" + Json(snapshot.Presence);
            if (folded != fresh) {
                Oracle.Fail($"{tab}: folded board view differs from a fresh snapshot\n  folded:   {folded}\n  snapshot: {fresh}");
            }
            return;
        }
        string actual;
        if (room.Poker is { } poker) {
            var (snapshot, overlay) = poker.View(room.Presence);
            var held = ((PokerOverlay)expected.O!).HeldByThisTab;
            actual = Json(expected with { D = snapshot, O = overlay with { HeldByThisTab = held } });
        } else if (room.Team is { } team) {
            actual = Json(expected with { D = team.View() });
        } else {
            Oracle.Fail($"{tab}: {room.Key.Kind} has no state after healing");
            return;
        }
        var wanted = Json(expected);
        if (actual != wanted) {
            Oracle.Fail($"{tab}: folded {room.Key.Kind} view differs from a fresh snapshot\n  folded:   {actual}\n  snapshot: {wanted}");
        }
    }

    /// <summary>Durability: what the live actors hold equals what a reload from the database builds.</summary>
    private async Task CheckDurableAsync() {
        var now = Clock.Now;
        var livePoker = await QueryAsync<PokerState, PokerInput, string>(
            PokerRoom, view => ValueTask.FromResult(Json(PokerRoom, PokerProjection.Snapshot(view.State!, []))));
        var loadedPoker = Json(PokerRoom, PokerProjection.Snapshot(Db.LoadPoker(now), []));
        if (livePoker != loadedPoker) {
            Oracle.Fail($"poker: the live room differs from a reload\n  live:   {livePoker}\n  reload: {loadedPoker}");
        }
        var liveTeam = await QueryAsync<TeamState, TeamInput, string>(
            TeamRoom, view => ValueTask.FromResult(Json(TeamRoom, TeamProjection.Snapshot(view.State!, now))));
        var loadedTeam = Json(TeamRoom, TeamProjection.Snapshot(Db.LoadTeam(), now));
        if (liveTeam != loadedTeam) {
            Oracle.Fail($"team: the live room differs from a reload\n  live:   {liveTeam}\n  reload: {loadedTeam}");
        }
        var liveBoard = await QueryAsync<RetroState, RetroInput, string>(
            BoardRoom, view => ValueTask.FromResult(Json(BoardRoom, RetroProjection.Snapshot(view.State!, []))));
        var loadedBoard = Json(BoardRoom, RetroProjection.Snapshot(Db.LoadRetro(), []));
        if (liveBoard != loadedBoard) {
            Oracle.Fail($"board: the live room differs from a reload\n  live:   {liveBoard}\n  reload: {loadedBoard}");
        }
    }

    private async Task<T> QueryAsync<TState, TInput, T>(RoomKey room, Func<RoomView<TState>, ValueTask<T>> query)
        where TState : class
        where TInput : class {
        var actor = (IRoomActor<TState, TInput>)await Process.Registry.GetOrStartAsync(room, CancellationToken.None);
        return await actor.QueryAsync(query, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(20));
    }

    private string Digest() {
        var outcomes = string.Join(",", Oracle.Intents.Select(x => $"{x.Id}:{x.Outcome}:{x.Detail}:{x.Commits}"));
        var rows = Json(PokerRoom, PokerProjection.Snapshot(Db.PokerRows, []))
            + Json(TeamRoom, TeamProjection.Snapshot(Db.LoadTeam(), Start))
            + Json(BoardRoom, RetroProjection.Snapshot(Db.RetroRows(), []));
        var bytes = System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes($"{Clock.Now:O}|{outcomes}|{rows}"));
        return Convert.ToHexString(bytes)[..16];
    }

    private static string Json(ServerFrame frame) => Encoding.UTF8.GetString(WireWriter.ToUtf8(frame));

    private static string Json(RoomKey room, RoomSnapshot snapshot) => Json(new SnapFrame(room, default, 0, 0, snapshot));

    private static string Json(IEnumerable<PrincipalInfo> presence) => string.Join(",", presence.Select(x => $"{x.Pid}:{x.Kind}:{x.Name}:{x.Avatar}"));

    private SimDb CreateDb() {
        var team = new TeamState(new TeamId(TeamRoom.Id), "Sim team");
        foreach (var (who, role) in new[] { (_alice, TeamRole.Owner), (_bob, TeamRole.Admin), (_carol, TeamRole.Member), (_dave, TeamRole.Member) }) {
            team.Members[who.Id] = new MemberInfo(who.Id, who.DisplayName, role);
        }
        var poker = new PokerState {
            SessionId = new SessionId(PokerRoom.Id),
            Code = "SIMSESS1",
            TeamId = team.TeamId,
            CreatedAt = Start.AddHours(-1),
            WarmUntil = Start,
            NextRoundNumber = 2,
        };
        var first = new Round { Id = new RoundId(NewGuid()), Number = 1 };
        poker.Rounds[first.Id] = first;
        poker.Order.Add(first.Id);
        poker.CurrentRoundId = first.Id;
        var board = Board(team.TeamId, [new ColumnId(NewGuid()), new ColumnId(NewGuid()), new ColumnId(NewGuid())]);
        var db = new SimDb(poker, team, board) { Bug = _options.Bug };
        for (var i = 0; i < 2; i++) {
            var card = new CardId(NewGuid());
            var text = $"seeded action {i}";
            db.InsertActionItem(
                new ActionItemInfo(new ActionItemId(NewGuid()), text, 1, Start.ToUnixTimeMilliseconds() - i, 0, new BoardId(NewGuid()), card, CreatedBy: _alice.Id),
                text);
        }
        return db;
    }

    /// <summary>
    /// A team board with stages and enforced readiness, anonymous posts allowed and author names shown, so an
    /// anonymous post is the only one without its author.
    /// </summary>
    private Func<RetroState> Board(TeamId team, ColumnId[] columns) {
        var id = new BoardId(BoardRoom.Id);
        return () => {
            var board = new RetroState {
                BoardId = id,
                Code = "SIMBOARD",
                TeamId = team,
                Title = "Sim retro",
                Config = RetroDefaults.Config with { StagesEnabled = true, EnforceReadiness = true },
            };
            string[] titles = ["Went well", "To improve", "Ideas"];
            for (var i = 0; i < columns.Length; i++) {
                board.Columns.Add(new Column { Id = columns[i], Title = titles[i], Color = (ColumnColor)i, SortKey = i });
            }
            return board;
        };
    }
}
