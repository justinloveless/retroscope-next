using FsCheck.Xunit;
using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using static Retroscope.Core.Tests.Poker.PokerHarness;

namespace Retroscope.Core.Tests.Poker;

/// <summary>
/// Random command sequences from up to six principals (plus a second tab), with joins, disconnects, time passing
/// and Jira results. After every step the state keeps its invariants and no projection leaks a vote; at the end
/// every viewer's fold of the projected frames equals a fresh snapshot.
/// </summary>
[Trait("Category", "Poker")]
public sealed class PokerPropertyTests {
    [Property(MaxTest = 300, EndSize = 120)]
    public void Team_sessions_keep_their_invariants(int[] ops) => new Run(PokerHarness.Team(), ops).Check();

    [Property(MaxTest = 200, EndSize = 120)]
    public void Quick_sessions_keep_their_invariants(int[] ops) => new Run(Quick(), ops).Check();

    private sealed class Run {
        private static readonly Viewer BobTab2 = Viewer(2, "Bob", tab: 1);
        private static readonly Viewer[] Actors = [Alice, Bob, Carol, Dave, Guest, Outsider, BobTab2];
        private static readonly Viewer[] Viewers = Actors[..6];

        private readonly PokerHarness _room;
        private readonly int[] _ops;
        private readonly Dictionary<PrincipalId, ViewerModel> _models = [];
        private int _nextId = 100;

        public Run(PokerHarness room, int[] ops) {
            _room = room;
            _ops = ops;
            _room.Applied = OnApplied;
            foreach (var actor in Viewers) {
                var snapshot = PokerProjection.Snapshot(_room.State, []);
                _models[actor.Who.Id] = new ViewerModel(snapshot, PokerProjection.Overlay(_room.State, actor.Who.Id, actor.Client), actor.Who.Id);
            }
        }

        public void Check() {
            for (var i = 0; i < _ops.Length; i++) {
                Step(Mix(_ops[i], i));
                Invariants.Hold(_room.State);
            }
            foreach (var actor in Viewers) {
                var expected = Snap(_room, actor);
                var (snapshot, overlay) = _models[actor.Who.Id].View(_room.PresenceInfo());
                var actual = WireJson(new SnapFrame(RoomKey.Poker(Session), default, 0, 0, snapshot, overlay with {
                    HeldByThisTab = PokerProjection.Overlay(_room.State, actor.Who.Id, actor.Client).HeldByThisTab,
                }));
                Assert.Equal(expected, actual);
                Secrecy.SnapshotHolds(_room.State, actor.Who.Id, actor.Client);
            }
        }

        private void OnApplied(IReadOnlyList<PokerEvent> events) {
            foreach (var actor in Viewers) {
                var wire = PokerProjection.Events(events, actor.Who.Id);
                Secrecy.FrameHolds(_room.State, actor.Who.Id, wire);
                _models[actor.Who.Id].Apply(wire);
            }
        }

        // FsCheck's ints are small; mixing each with its position spreads them over every actor, kind and argument
        // while keeping a failing input replayable and shrinkable.
        private static uint Mix(int op, int index) {
            var value = unchecked((uint)op * 2654435761u) ^ unchecked((uint)index * 2246822519u);
            value ^= value >> 15;
            return unchecked(value * 2654435761u) ^ (value >> 13);
        }

        private void Step(uint value) {
            var actor = Actors[(int)(value % (uint)Actors.Length)];
            var kind = (int)(value / 7 % 30);
            var arg = (int)(value / 210 % 100_000);
            if (kind == 6 && arg % 2 == 1 && _room.State.Spotlight is { } spotlight) {
                actor = Actors.FirstOrDefault(x => x.Who.Id == spotlight.Holder && (spotlight.HolderClient ?? x.Client) == x.Client) ?? actor;
            }
            if (kind is 12 or 13 && arg % 2 == 0 && PendingRound(arg) is { } preview) {
                var owner = _room.State.Rounds[preview].PendingOwner;
                actor = Actors.FirstOrDefault(x => x.Who.Id == owner) ?? actor;
            }
            var input = Command(kind, arg, actor);
            if (input is null) {
                return;
            }
            var digest = Digest.Of(_room.State);
            var decision = _room.Decide(actor, input);
            Assert.Equal(digest, Digest.Of(_room.State));
            if (decision is not Decision<PokerEvent>.Accept accept) {
                return;
            }
            _room.Commit(accept.Events);
            // A create that only filled the placeholder leaves its round id unused, so only the persisted cursor
            // (§c.8) keeps its redelivery from creating a round; every other command is idempotent on its own.
            var filledOnly = input is ClientCommand { Command: PokerRoundCreate or PokerRoundCreateMany }
                && !accept.Events.Any(x => x is RoundCreated);
            if (input is ClientCommand && !filledOnly) {
                var again = _room.Decide(actor, input);
                Assert.True(
                    again is not Decision<PokerEvent>.Accept,
                    $"redelivering {input} was accepted again: {again}");
            }
        }

        private PokerInput? Command(int kind, int arg, Viewer actor) {
            var round = PickRound(arg);
            var points = Deck.Values[arg % Deck.Values.Length];
            switch (kind) {
                case 0:
                case 1:
                    return new PokerVoteSet(round, points, arg % 5 == 0 ? Deck.Next(points) : null, arg % 3 == 0 ? true : null);
                case 2:
                    return new PokerLockSet(round, arg % 4 != 0);
                case 3:
                    return new PokerAbstainSet(round, arg % 3 != 0);
                case 4:
                    return new PokerRoundReveal(round);
                case 5:
                    return new PokerRoundReplay(round);
                case 6:
                    return new PokerRoundCreate(NewRound(), Ticket(arg / 2), arg % 2 == 1 ? true : null, arg % 3 == 0 ? true : null);
                case 7:
                    return new PokerRoundCreateMany(
                        Enumerable.Range(0, 1 + (arg % 3)).Select(x => new NewRound(NewRound(), Ticket(arg + x))).ToArray());
                case 8:
                    return new PokerRoundNext(round, NewRound(), Ticket(arg));
                case 9:
                    return new PokerRoundEnd(round);
                case 10:
                    return new PokerRoundActivate(round);
                case 11:
                    var version = _room.State.Rounds.TryGetValue(round, out var found) ? found.Version : 1;
                    return new PokerRoundSetTicket(round, Ticket(arg), arg % 5 == 0 ? version - 1 : version);
                case 12:
                    return new PokerRoundCommitPending(PendingRound(arg) ?? round);
                case 13:
                    return new PokerRoundDiscardPending(PendingRound(arg) ?? round);
                case 14:
                    return new PokerRoundDelete(round);
                case 15:
                    var order = _room.State.Order.ToArray();
                    new Random(arg).Shuffle(order);
                    return new PokerRoundReorder(arg % 7 == 0 ? order.Skip(1).ToArray() : order);
                case 16:
                    return arg % 3 == 0 ? new ClientCommand(new PokerRoundsClearAll(NewRound())) : null;
                case 17:
                    return arg % 2 == 0
                        ? new PokerSettingsUpdate(new PokerSettingsValues(PresenceEnabled: _room.State.PresenceEnabled), PresenceEnabled: !_room.State.PresenceEnabled)
                        : new PokerSettingsUpdate(new PokerSettingsValues(FollowSpotlight: arg % 3 == 0), FollowSpotlight: arg % 5 == 0);
                case 18:
                    return new PokerObserverSet(Actors[arg % 6].Who.Id, arg % 3 == 0);
                case 19:
                    return new PokerSpotlightTake(round, arg % 2 == 0);
                case 20:
                case 21:
                    return new PokerSpotlightMove(round);
                case 22:
                    return new PokerSpotlightGive(Actors[arg % 6].Who.Id);
                case 23:
                    return new PokerSpotlightRelease();
                case 24:
                    return new PokerJiraSubmitPoints(round, points);
                case 25:
                    return _room.State.JiraSubmits.FirstOrDefault() is { Value.Status: JiraSubmitStatus.Pending } pending
                        ? new JiraPointsResult(pending.Key, pending.Value.Key, pending.Value.Points, pending.Value.By, arg % 3 != 0, "jira_error")
                        : null;
                case 26:
                    if (_room.Presence.ClientsOf(actor.Who.Id).Contains(actor.Client)) {
                        _room.Disconnect(actor);
                    } else {
                        _room.Join(actor);
                    }
                    return null;
                case 27:
                    _room.Advance(TimeSpan.FromMilliseconds(arg % 4 == 0 ? 200_000 : 50 * (arg % 20)));
                    return null;
                case 28:
                    return new JiraMetaLoaded(
                        _room.State.Rounds.Values.Where(x => x.TicketKey is not null)
                            .Select(x => new JiraMeta(x.TicketKey!, ParentKey: arg % 2 == 0 ? $"EPIC-{1 + (arg % 3)}" : null))
                            .ToArray());
                default:
                    return arg % 97 == 0 ? new DeleteSession() : null;
            }
        }

        private RoundId PickRound(int arg) {
            var order = _room.State.Order;
            return order.Count == 0 || arg % 17 == 16 ? R(99) : order[arg % order.Count];
        }

        private RoundId? PendingRound(int arg) {
            var pending = _room.State.Order.Where(x => _room.State.Rounds[x].Pending).ToArray();
            return pending.Length == 0 || arg % 5 == 0 ? null : pending[arg % pending.Length];
        }

        private RoundId NewRound() => R(_nextId++);

        private static string? Ticket(int arg) => (arg % 4) switch {
            0 => null,
            1 => $"ABC-{1 + (arg % 9)}",
            2 => $"{1 + (arg % 9)}",
            _ => "xyz 1",
        };
    }

    internal static string Snap(PokerHarness room, Viewer viewer) {
        var snapshot = PokerProjection.Snapshot(room.State, room.PresenceInfo());
        var overlay = PokerProjection.Overlay(room.State, viewer.Who.Id, viewer.Client);
        return WireJson(new SnapFrame(RoomKey.Poker(Session), default, 0, 0, snapshot, overlay));
    }

    internal static string WireJson(ServerFrame frame) => System.Text.Encoding.UTF8.GetString(WireWriter.ToUtf8(frame));
}
