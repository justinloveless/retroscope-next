using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;

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

/// <summary>Open issues the simulation found in other units; see <c>docs/decisions/U15.md</c>.</summary>
internal static class KnownIssues {
    /// <summary>
    /// A command refused with a retryable nack (server_busy) whose nack is lost with the socket, while a later
    /// command of the same cid committed: the resend is acked <c>dup</c> and the intent is silently lost.
    /// </summary>
    public const string LostRetryableNack = "lost-retryable-nack";
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

    public IntentOutcome Outcome { get; set; }

    /// <summary>The server refused it with a retryable nack that the client has not read yet.</summary>
    public bool UnreadRetryableNack { get; set; }

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

    public void RetryableNackSent(RoomKey room, ClientId client, long q) {
        lock (_lock) {
            if (_bySend.TryGetValue((room, client, q), out var intent)) {
                intent.UnreadRetryableNack = true;
            }
        }
    }

    public void RetryableNackRead(Intent intent) {
        lock (_lock) {
            intent.UnreadRetryableNack = false;
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
                }
            }
        }
    }

    public void Acked(Intent intent, bool dup) {
        lock (_lock) {
            if (intent.Outcome is IntentOutcome.Nacked && !dup) {
                Fail($"{intent} applied after a final nack ({intent.Detail})");
            }
            if (dup) {
                // dup means "this q was processed": a resend whose nack was lost is acked dup too. Only an intent
                // the server never decided at all has been skipped.
                if (intent.Commits == 0 && intent.LastDecision == DecisionSeen.None) {
                    if (intent.UnreadRetryableNack) {
                        FailKnown(KnownIssues.LostRetryableNack);
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
