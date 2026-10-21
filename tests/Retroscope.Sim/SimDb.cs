using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Core.Team;
using Retroscope.Server.Data;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Realtime.Actors;

namespace Retroscope.Sim;

/// <summary>What the next transaction of a room does instead of committing normally.</summary>
internal enum CommitFault : byte {
    None,

    /// <summary>The connection broke before COMMIT: nothing landed, the actor cannot tell (poison).</summary>
    FailedBeforeCommit,

    /// <summary>COMMIT landed but the answer was lost: the client saw an error for a committed command (R2).</summary>
    OutcomeUnknownLanded,

    /// <summary>COMMIT never reached the server and the connection broke.</summary>
    OutcomeUnknownLost,

    /// <summary>A constraint refused the batch: a bug, nacked <c>internal</c>.</summary>
    Rejected,

    /// <summary>Another instance bumped the epoch; this one must exit (§c.9).</summary>
    FencingLost,

    /// <summary>The process is killed after COMMIT and before the ack or the fan-out.</summary>
    CrashAfterCommit,
}

/// <summary>
/// The database: one poker session and its team, as their rows would load. Committed events are applied to a
/// private copy of each state, so a reload sees exactly what committed and nothing that was only in memory.
/// </summary>
internal sealed class SimDb {
    private readonly Lock _lock = new();
    private readonly Dictionary<(RoomKey, ClientId), (PrincipalId Principal, long LastQ)> _cursors = [];
    private readonly Dictionary<RoomKey, CommitFault> _faults = [];
    private readonly PokerState _poker;
    private readonly TeamState _team;

    public SimDb(PokerState poker, TeamState team) {
        _poker = poker.Clone();
        _team = Clone(team);
    }

    /// <summary>The board side of action items: card id → (body, version), for the text-sync repair on load.</summary>
    public Dictionary<CardId, (string Text, int Version)> Cards { get; } = [];

    public InjectedBug Bug { get; init; }

    public Lock Sync => _lock;

    public void Arm(RoomKey room, CommitFault fault) {
        lock (_lock) {
            _faults[room] = fault;
        }
    }

    public CommitFault TakeFault(RoomKey room) {
        lock (_lock) {
            return _faults.Remove(room, out var fault) ? fault : CommitFault.None;
        }
    }

    public IReadOnlyList<MemberInfo> Members {
        get {
            lock (_lock) {
                return [.. _team.Members.Values];
            }
        }
    }

    public PokerState PokerRows {
        get {
            lock (_lock) {
                return _poker.Clone();
            }
        }
    }

    /// <summary>The poker row set as <c>PokerStore.LoadAsync</c> (U21) builds the state: memory-only parts start empty.</summary>
    public PokerState LoadPoker(DateTimeOffset now) {
        lock (_lock) {
            var state = _poker.Clone();
            state.WarmUntil = now + PokerTiming.WarmUp;
            state.Roster.Clear();
            foreach (var member in _team.Members.Values) {
                state.Roster[member.Pid] = new PrincipalInfo(member.Pid, PrincipalKind.User, member.Name);
            }
            if (state.Spotlight is { } spotlight) {
                spotlight.HolderClient = null;
                spotlight.Dirty = false;
                spotlight.MovedAt = default;
                spotlight.HolderAbsentSince = null;
            }
            foreach (var round in state.Rounds.Values) {
                round.AutoRevealGen = 0;
                round.ArmedAt = default;
            }
            state.JiraSubmits.Clear();
            return state;
        }
    }

    /// <summary>The team as <c>TeamStore.LoadAsync</c> (U23) builds it, including the text-sync repair of §c.2.</summary>
    public TeamState LoadTeam() {
        lock (_lock) {
            foreach (var item in _team.ActionItems.Values.ToList()) {
                if (item.SourceCardId is { } card && Cards.TryGetValue(card, out var source) && source.Version > item.SourceVersion) {
                    _team.ActionItems[item.Id] = item with { Text = source.Text, SourceVersion = source.Version };
                }
            }
            return Clone(_team);
        }
    }

    public ClientCursor Cursor(RoomKey room, ClientId client, PrincipalId principal) {
        lock (_lock) {
            if (!_cursors.TryGetValue((room, client), out var cursor)) {
                return new ClientCursor(CursorOwner.None, 0);
            }
            if (cursor.Principal != principal) {
                return new ClientCursor(CursorOwner.OtherPrincipal, 0);
            }
            var lastQ = Bug == InjectedBug.Cursor ? Math.Max(0, cursor.LastQ - 1) : cursor.LastQ;
            return new ClientCursor(CursorOwner.Caller, lastQ);
        }
    }

    /// <summary>Writes the cursor with <c>greatest()</c>; never rebinds another principal's cid (U12 decision 2).</summary>
    public void AdvanceCursor(RoomKey room, CursorAdvance advance) {
        if (_cursors.TryGetValue((room, advance.Client), out var cursor)) {
            if (cursor.Principal == advance.Principal) {
                _cursors[(room, advance.Client)] = (cursor.Principal, Math.Max(cursor.LastQ, advance.Q));
            }
        } else {
            _cursors[(room, advance.Client)] = (advance.Principal, advance.Q);
        }
    }

    /// <summary>
    /// The rows after <paramref name="evt"/>. <see cref="SpotlightSettled"/> writes the round and the bound tab
    /// (the live state already holds them from the ephemeral moves, so <see cref="PokerApply"/> only clears the flag).
    /// </summary>
    public void ApplyPoker(PokerEvent evt) {
        if (evt is SpotlightSettled settled && _poker.Spotlight is { } spotlight) {
            spotlight.RoundId = settled.RoundId;
            spotlight.HolderClient = settled.HolderClient;
        }
        PokerApply.Apply(_poker, evt);
    }

    /// <summary>Column-scoped writes, as each <see cref="TeamEvent"/> documents (R24): an update never resurrects a row.</summary>
    public bool ApplyTeam(TeamEvent evt) {
        switch (evt) {
            case TeamEvent.ActionItemAssigned e:
                Update(e.Item.Id, x => x with { AssignedTo = e.Item.AssignedTo });
                return false;
            case TeamEvent.ActionItemDoneSet e:
                Update(e.Item.Id, x => x with { DoneAt = e.Item.DoneAt, DoneBy = e.Item.DoneBy });
                return false;
            case TeamEvent.ActionItemDeleted e:
                _team.ActionItems.Remove(e.ItemId);
                return false;
            case TeamEvent.ActionItemTextSynced e:
                Update(e.ItemId, x => x.SourceVersion < e.SourceVersion ? x with { Text = e.Text, SourceVersion = e.SourceVersion } : x);
                return false;
            case TeamEvent.MemberAdded or TeamEvent.MemberRoleSet or TeamEvent.MemberRemoved:
                TeamApply.Apply(_team, evt);
                return true;
            default:
                return false;
        }
    }

    /// <summary>The board actor's transaction: the action item is created in place (§c.2).</summary>
    public void InsertActionItem(ActionItemInfo item, string text) {
        lock (_lock) {
            _team.ActionItems[item.Id] = item;
            Cards[item.SourceCardId!.Value] = (text, item.SourceVersion);
        }
    }

    public void EditCard(CardId card, string text, int version) {
        lock (_lock) {
            Cards[card] = (text, version);
        }
    }

    /// <summary>The board's delete sets <c>source_card_id</c> to null in its own transaction.</summary>
    public void DeleteCard(CardId card) {
        lock (_lock) {
            Cards.Remove(card);
            foreach (var item in _team.ActionItems.Values.Where(x => x.SourceCardId == card).ToList()) {
                _team.ActionItems[item.Id] = item with { SourceCardId = null };
            }
        }
    }

    private void Update(ActionItemId id, Func<ActionItemInfo, ActionItemInfo> change) {
        if (_team.ActionItems.TryGetValue(id, out var item)) {
            _team.ActionItems[id] = change(item);
        }
    }

    public static TeamState Clone(TeamState team) {
        var clone = new TeamState(team.TeamId, team.Name) {
            Deleted = team.Deleted,
            PointDescriptions = team.PointDescriptions,
            MaxEndorsements = team.MaxEndorsements,
        };
        foreach (var (pid, member) in team.Members) {
            clone.Members[pid] = member;
        }
        foreach (var (id, item) in team.ActionItems) {
            clone.ActionItems[id] = item;
        }
        clone.EndorsementTypes.AddRange(team.EndorsementTypes);
        return clone;
    }
}

/// <summary>
/// One room kind's store: loads from <see cref="SimDb"/>, checks the commit against the oracle, applies it, then
/// plays the injected fault. Instances belong to one server process; a dead process never writes.
/// </summary>
internal abstract class SimStore<TState, TEvent>(World world, SimProcess process) : IRoomStore<TState, TEvent>
    where TState : class
    where TEvent : notnull {
    protected World World => world;

    protected SimProcess Process => process;

    protected SimDb Db => world.Db;

    public abstract Task<TState?> LoadAsync(RoomKey room, DateTimeOffset now, CancellationToken cancellationToken);

    public Task<ClientCursor> LoadCursorAsync(RoomKey room, ClientId client, PrincipalId principal, CancellationToken cancellationToken) {
        return Task.FromResult(Db.Cursor(room, client, principal));
    }

    public Task CommitAsync(RoomKey room, TState state, IReadOnlyList<TEvent> events, CursorAdvance? cursor, CancellationToken cancellationToken) {
        if (process.Dead) {
            throw new FencingLostException();
        }
        var fault = Db.TakeFault(room);
        if (fault == CommitFault.Rejected && cursor is null) {
            // A constraint violation is a bug in the command's SQL; internal transitions are not where it is modelled.
            Db.Arm(room, fault);
            fault = CommitFault.None;
        }
        var intent = cursor is { } advance ? world.Oracle.IntentOf(room, advance.Client, advance.Q) : null;
        switch (fault) {
            case CommitFault.FailedBeforeCommit:
                throw new InvalidOperationException("simulated: the connection broke before COMMIT");
            case CommitFault.OutcomeUnknownLost:
                throw new CommitOutcomeUnknownException(new IOException("simulated: COMMIT was never sent"));
            case CommitFault.Rejected:
                throw new CommitRejectedException("23505", new InvalidOperationException("simulated constraint violation"));
            case CommitFault.FencingLost:
                process.FencingLost = true;
                throw new FencingLostException();
        }
        bool posts;
        lock (Db.Sync) {
            CheckPreconditions(intent, events);
            posts = Apply(events);
            if (cursor is { } landed) {
                Db.AdvanceCursor(room, landed);
            }
            world.Oracle.Committed(intent, room, [.. events.Cast<object>()]);
        }
        switch (fault) {
            case CommitFault.OutcomeUnknownLanded:
                throw new CommitOutcomeUnknownException(new IOException("simulated: the COMMIT answer was lost"));
            case CommitFault.CrashAfterCommit:
                process.RequestCrash();
                return new TaskCompletionSource().Task;
        }
        if (posts) {
            AfterCommit(events);
        }
        return Task.CompletedTask;
    }

    /// <summary>Applies the committed events to the rows; true when other actors must hear about it.</summary>
    protected abstract bool Apply(IReadOnlyList<TEvent> events);

    /// <summary>Precondition oracle (R5): the rows before this commit must match what the command expected.</summary>
    protected virtual void CheckPreconditions(Intent? intent, IReadOnlyList<TEvent> events) {
    }

    /// <summary>Actor-to-actor posts after a successful commit (always non-blocking, §c.2).</summary>
    protected virtual void AfterCommit(IReadOnlyList<TEvent> events) {
    }
}

internal sealed class PokerSimStore(World world, SimProcess process) : SimStore<PokerState, PokerEvent>(world, process) {
    public override Task<PokerState?> LoadAsync(RoomKey room, DateTimeOffset now, CancellationToken cancellationToken) {
        return Task.FromResult<PokerState?>(Db.LoadPoker(now));
    }

    protected override bool Apply(IReadOnlyList<PokerEvent> events) {
        foreach (var evt in events) {
            Db.ApplyPoker(evt);
        }
        return events.Any(x => x is RoundRevealed);
    }

    protected override void CheckPreconditions(Intent? intent, IReadOnlyList<PokerEvent> events) {
        var rows = Db.PokerRows;
        switch (intent?.Command) {
            case PokerRoundSetTicket set when events.Any(x => x is RoundUpdated u && u.Round.Id == set.RoundId):
                if (rows.Rounds.TryGetValue(set.RoundId, out var round) && round.Version != set.BaseVersion) {
                    World.Oracle.Fail($"stale overwrite accepted: setTicket with baseVersion {set.BaseVersion} over version {round.Version} ({intent})");
                }
                break;
            case PokerSettingsUpdate update when events.Any(x => x is SettingsChanged):
                if ((update.PresenceEnabled is not null && update.Expected.PresenceEnabled != rows.PresenceEnabled)
                    || (update.FollowSpotlight is not null && update.Expected.FollowSpotlight != rows.FollowSpotlight)) {
                    World.Oracle.Fail($"stale overwrite accepted: settings.update expected {update.Expected} over ({rows.PresenceEnabled}, {rows.FollowSpotlight}) ({intent})");
                }
                break;
            case PokerRoundNext next when events.Any(x => x is RoundCreated):
                if (!rows.Rounds.TryGetValue(next.FromRoundId, out var from) || !from.Active) {
                    World.Oracle.Fail($"stale overwrite accepted: round.next from a round that is no longer active ({intent})");
                }
                break;
            case PokerRoundReorder reorder when events.Any(x => x is RoundsReordered):
                if (!reorder.Order.OrderBy(x => x.Value).SequenceEqual(rows.Order.OrderBy(x => x.Value))) {
                    World.Oracle.Fail($"stale overwrite accepted: reorder that is not a permutation of the rounds ({intent})");
                }
                break;
        }
    }

    protected override void AfterCommit(IReadOnlyList<PokerEvent> events) {
        var rows = Db.PokerRows;
        var pointed = rows.Rounds.Values.Count(x => x.State == RoundState.Revealed);
        var changed = new TeamInput.PokerChanged(rows.SessionId, pointed, rows.Rounds.Count);
        World.PostLater(Process, registry => registry.Post<TeamState, TeamInput>(World.TeamRoom, changed));
    }
}

internal sealed class TeamSimStore(World world, SimProcess process) : SimStore<TeamState, TeamEvent>(world, process) {
    /// <summary>
    /// Loading also refreshes the membership cache and the poker roster: a member change whose commit outcome was
    /// unknown landed without its fan-out, and nothing else would ever repair the live poker room.
    /// </summary>
    public override Task<TeamState?> LoadAsync(RoomKey room, DateTimeOffset now, CancellationToken cancellationToken) {
        var state = Db.LoadTeam();
        FanOut();
        return Task.FromResult<TeamState?>(state);
    }

    protected override bool Apply(IReadOnlyList<TeamEvent> events) {
        var membership = false;
        foreach (var evt in events) {
            membership |= Db.ApplyTeam(evt);
        }
        return membership;
    }

    protected override void AfterCommit(IReadOnlyList<TeamEvent> events) => FanOut();

    /// <summary>U23: bump the membership cache, refresh the poker roster and re-check every subscription.</summary>
    private void FanOut() {
        var members = Db.Members;
        World.Access.Set(members);
        var seats = members.Select(x => new PrincipalInfo(x.Pid, PrincipalKind.User, x.Name)).ToList();
        World.PostLater(Process, registry => registry.Post<PokerState, PokerInput>(World.PokerRoom, new RosterChanged(seats)));
        World.PostLater(Process, registry => registry.AccessChanged(World.PokerRoom));
        World.PostLater(Process, registry => registry.AccessChanged(World.TeamRoom));
    }
}
