using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;

namespace Retroscope.Sim;

internal enum IntentOutcome : byte {
    Pending,
    Acked,
    Nacked,

    /// <summary>The client gave it up by rule: an ephemeral kind older than 10 s, or its room was kicked.</summary>
    Dropped,

    /// <summary>An injected storage fault threw it away; nothing is promised about it.</summary>
    Lost,
}

internal enum DecisionSeen : byte {
    None,
    Rejected,
    NoOp,

    /// <summary>Accepted with only ephemeral or memory-only events: applied without a transaction.</summary>
    AppliedInMemory,
    Committed,
}

/// <summary>One thing a user did. Every resend, renumbering and copy of the outbox entry is the same intent.</summary>
internal sealed class Intent(int id, RoomKey room, Command command, DateTimeOffset issuedAt, bool ephemeral) {
    public int Id { get; } = id;

    public RoomKey Room { get; } = room;

    public Command Command { get; } = command;

    public string Kind { get; } = Catalog.KindOf(command);

    public DateTimeOffset IssuedAt { get; } = issuedAt;

    public bool Ephemeral { get; } = ephemeral;

    /// <summary>Transactions that carried this intent's cursor (more than one is a double application).</summary>
    public int Commits { get; set; }

    public DecisionSeen LastDecision { get; set; }

    /// <summary>The server process that last decided it (<see cref="Oracle.Generation"/>).</summary>
    public int DecidedIn { get; set; } = -1;

    public IntentOutcome Outcome { get; set; }

    /// <summary>
    /// Storage went back to a copy that still held it after it was answered: the tab sends it again, and its new
    /// answer is not judged against the old one (a second commit still is).
    /// </summary>
    public bool Resurrected { get; set; }

    /// <summary>
    /// Two live tabs hold it (a twin copied the outbox): each tab's answer is about its own send, so an ack in one after
    /// a final nack in the other is not judged against it (a second commit still is).
    /// </summary>
    public bool Shared { get; set; }

    public string? Detail { get; set; }

    public uint DataHash { get; set; }

    public override string ToString() => $"#{Id} {Kind} on {Room.Kind}";
}

/// <summary>
/// Everything the world checks while it runs and at the end (DESIGN §j): exactly-once per intent, acks that tell
/// the truth, preconditions honoured by every commit, gapless seqs, presence grace, one reveal per voting phase.
/// Callbacks arrive from actor threads, so every member locks.
/// </summary>
internal sealed class Oracle(SimClock clock) {
    private const int MaxViolations = 20;

    private readonly Lock _lock = new();
    private readonly List<string> _violations = [];
    private readonly Dictionary<string, int> _known = [];
    private readonly List<Intent> _intents = [];
    private readonly Dictionary<Command, Intent> _byCommand = new(ReferenceEqualityComparer.Instance);
    private readonly Dictionary<(RoomKey, ClientId, long), Intent> _bySend = [];
    private readonly Dictionary<(RoomKey, ClientId, long), List<Intent>> _sentUnder = [];
    private readonly HashSet<ClientId> _twinned = [];
    private readonly Dictionary<RoomKey, List<(long Ms, PrincipalId Principal)>> _leaves = [];
    private readonly Dictionary<(RoomKey, PrincipalId), List<Interval>> _connected = [];
    private readonly Dictionary<(RoomKey, ConnectionId), Interval> _open = [];
    private readonly Dictionary<RoundId, int> _revealsSinceVoting = [];

    public int Step { get; set; }

    public IReadOnlyList<string> Violations {
        get {
            lock (_lock) {
                return [.. _violations];
            }
        }
    }

    /// <summary>
    /// Violations that match an open issue recorded in <c>docs/decisions/U15.md</c>, by name. They are reported on
    /// every run and fail it only with <c>--strict</c>, until the owning unit fixes them.
    /// </summary>
    public IReadOnlyDictionary<string, int> Known {
        get {
            lock (_lock) {
                return new Dictionary<string, int>(_known);
            }
        }
    }

    public bool Failed {
        get {
            lock (_lock) {
                return _violations.Count > 0;
            }
        }
    }

    public IReadOnlyList<Intent> Intents {
        get {
            lock (_lock) {
                return [.. _intents];
            }
        }
    }

    public int Commits { get; private set; }

    public int AutoReveals { get; private set; }

    public int TimerExpiries { get; private set; }

    /// <summary>Server processes started so far; the payload-hash LRU of §c.6 lives and dies with one.</summary>
    public int Generation { get; private set; }

    /// <summary>Twin intents acked <c>dup</c> undecided because a restart emptied the LRU (the §c.6 residual).</summary>
    public int TwinResiduals { get; private set; }

    public void Fail(string message) {
        lock (_lock) {
            if (_violations.Count < MaxViolations) {
                _violations.Add($"step {Step}, t+{(clock.Now - World.Start).TotalMilliseconds:0}ms: {message}");
            }
        }
    }

    public void FailKnown(string issue) {
        lock (_lock) {
            _known[issue] = _known.GetValueOrDefault(issue) + 1;
        }
    }

    public void Register(Intent intent) {
        lock (_lock) {
            _intents.Add(intent);
            _byCommand[intent.Command] = intent;
        }
    }

    public void Transmitted(Intent intent, ClientId client, long q) {
        lock (_lock) {
            _bySend[(intent.Room, client, q)] = intent;
            if (!_sentUnder.TryGetValue((intent.Room, client, q), out var sent)) {
                _sentUnder[(intent.Room, client, q)] = sent = [];
            }
            if (!sent.Contains(intent)) {
                sent.Add(intent);
            }
        }
    }

    /// <summary>Two live tabs now hold <paramref name="client"/>: a duplicate whose original stayed open.</summary>
    public void Twinned(ClientId client) {
        lock (_lock) {
            _twinned.Add(client);
        }
    }

    public void Restarted() {
        lock (_lock) {
            Generation++;
        }
    }

    public Intent? IntentOf(RoomKey room, ClientId client, long q) {
        lock (_lock) {
            return _bySend.GetValueOrDefault((room, client, q));
        }
    }

    public Action<string>? Trace { get; set; }

    public void Decided(Command command, DecisionSeen decision) {
        lock (_lock) {
            if (_byCommand.TryGetValue(command, out var intent)) {
                Trace?.Invoke($"server: decided {intent}: {decision}");
                intent.LastDecision = decision;
                intent.DecidedIn = Generation;
            }
        }
    }

    /// <summary>A transaction landed in the database (called by the stores, on the actor thread).</summary>
    public void Committed(Intent? intent, RoomKey room, IReadOnlyList<object> events) {
        lock (_lock) {
            Commits++;
            if (intent is not null) {
                intent.Commits++;
                if (intent.Commits > 1) {
                    Fail($"double application: {intent} committed {intent.Commits} times");
                }
            }
            foreach (var evt in events) {
                switch (evt) {
                    case RoundRevealed revealed:
                        if (revealed.By is null) {
                            AutoReveals++;
                        }
                        var reveals = _revealsSinceVoting.GetValueOrDefault(revealed.RoundId) + 1;
                        _revealsSinceVoting[revealed.RoundId] = reveals;
                        if (reveals > 1) {
                            Fail($"round {revealed.RoundId} revealed {reveals} times without a replay (by {revealed.By?.ToString() ?? "auto-reveal"})");
                        }
                        break;
                    case RoundReplayed replayed:
                        _revealsSinceVoting[replayed.Round.Id] = 0;
                        break;
                    case TimerChanged { ExpiredAt: not null }:
                        TimerExpiries++;
                        break;
                }
            }
        }
    }

    public void Acked(Intent intent, bool dup, ClientId client, long q) {
        lock (_lock) {
            if (intent.Outcome is IntentOutcome.Nacked && !dup && !intent.Resurrected && !intent.Shared) {
                Fail($"{intent} applied after a final nack ({intent.Detail})");
            }
            if (dup) {
                // dup means "this q was processed": a resend whose nack was lost is acked dup too. Only an intent
                // the server never decided at all has been skipped.
                if (intent.Commits == 0 && intent.LastDecision == DecisionSeen.None) {
                    if (IsTwinResidual(intent, client, q)) {
                        TwinResiduals++;
                        Trace?.Invoke($"oracle: {intent} acked dup undecided: its twin's intent under q={q} was decided before a restart (§c.6 residual)");
                    } else {
                        Fail($"{intent} acked dup but the server never decided it: the intent is lost");
                    }
                }
            } else if (intent.LastDecision == DecisionSeen.Committed && intent.Commits == 0) {
                Fail($"{intent} acked but its transaction never committed");
            } else if (intent.LastDecision is DecisionSeen.None or DecisionSeen.Rejected) {
                Fail($"{intent} acked without an accepting decision ({intent.LastDecision})");
            }
            if (intent.Outcome != IntentOutcome.Lost) {
                intent.Outcome = IntentOutcome.Acked;
            }
        }
    }

    public void Nacked(Intent intent, NackCode code) {
        lock (_lock) {
            if (intent.Commits > 0) {
                Fail($"{intent} committed but was answered with the final nack {code}");
            }
            if (intent.Outcome == IntentOutcome.Pending) {
                intent.Outcome = IntentOutcome.Nacked;
                intent.Detail = code.ToString();
            }
        }
    }

    public void Dropped(Intent intent, string why) {
        lock (_lock) {
            if (intent.Outcome == IntentOutcome.Pending) {
                intent.Outcome = IntentOutcome.Dropped;
                intent.Detail = why;
            }
        }
    }

    public void Lost(Intent intent, string why) {
        lock (_lock) {
            if (intent.Outcome == IntentOutcome.Pending) {
                intent.Outcome = IntentOutcome.Lost;
                intent.Detail = why;
            }
        }
    }

    /// <summary>
    /// §c.6 accepts one way to lose an intent: two tabs share a cid (a twin), one of them has another intent decided
    /// under the same q, and the server restarts before the other sends this one, so the payload-hash LRU that would
    /// refuse it <c>cid_reuse</c> is empty. Anything else acked <c>dup</c> undecided is a violation.
    /// </summary>
    private bool IsTwinResidual(Intent intent, ClientId client, long q) {
        if (!_twinned.Contains(client) || !_sentUnder.TryGetValue((intent.Room, client, q), out var sent)) {
            return false;
        }
        var decided = sent.Where(x => x != intent && x.LastDecision != DecisionSeen.None).ToList();
        return decided.Count > 0 && decided.All(x => x.DecidedIn < Generation);
    }

    /// <summary>The server answered a subscription on this connection: the principal is connected to the room.</summary>
    public void Connected(RoomKey room, ConnectionId connection, PrincipalId principal) {
        lock (_lock) {
            if (_open.ContainsKey((room, connection))) {
                return;
            }
            var interval = new Interval(clock.Now.ToUnixTimeMilliseconds());
            _open[(room, connection)] = interval;
            if (!_connected.TryGetValue((room, principal), out var list)) {
                _connected[(room, principal)] = list = [];
            }
            list.Add(interval);
        }
    }

    public void Disconnected(RoomKey room, ConnectionId connection) {
        lock (_lock) {
            if (_open.Remove((room, connection), out var interval)) {
                interval.EndMs = clock.Now.ToUnixTimeMilliseconds();
            }
        }
    }

    public void PresenceLeft(RoomKey room, PrincipalId principal) {
        lock (_lock) {
            if (!_leaves.TryGetValue(room, out var list)) {
                _leaves[room] = list = [];
            }
            list.Add((clock.Now.ToUnixTimeMilliseconds(), principal));
        }
    }

    /// <summary>A leave must come after the principal was disconnected from the room for the whole grace period.</summary>
    public void CheckPresence(TimeSpan grace) {
        lock (_lock) {
            var graceMs = (long)grace.TotalMilliseconds;
            foreach (var (room, leaves) in _leaves) {
                foreach (var (at, principal) in leaves.Distinct()) {
                    var intervals = _connected.GetValueOrDefault((room, principal)) ?? [];
                    if (intervals.Any(x => x.StartMs <= at && (x.EndMs ?? long.MaxValue) > at - graceMs)) {
                        var origin = World.Start.ToUnixTimeMilliseconds();
                        var spans = string.Join(", ", intervals.Select(x => $"[{x.StartMs - origin}, {(x.EndMs is { } end ? end - origin : "open")})"));
                        Fail($"presence.leave for {principal} in {room.Kind} at t+{at - origin}ms although it was connected within the {grace.TotalSeconds:0} s grace (connected: {spans})");
                    }
                }
            }
        }
    }

    private sealed class Interval(long startMs) {
        public long StartMs { get; } = startMs;

        public long? EndMs { get; set; }
    }
}
