using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;

namespace Retroscope.Sim;

/// <summary>
/// What people click, built from what their tab currently shows: a lagging view produces stale preconditions,
/// votes on rounds that were just revealed and moves of a spotlight someone else took, as in real use.
/// </summary>
internal static class Workload {
    private static readonly (int Weight, PokerKind Kind)[] PokerMix = [
        (24, PokerKind.Vote), (6, PokerKind.Lock), (4, PokerKind.Abstain), (6, PokerKind.Reveal), (3, PokerKind.Replay),
        (4, PokerKind.Create), (2, PokerKind.CreateMany), (4, PokerKind.Next), (2, PokerKind.End), (2, PokerKind.Activate),
        (9, PokerKind.SetTicket), (3, PokerKind.Reorder), (4, PokerKind.Settings), (2, PokerKind.Observer),
        (4, PokerKind.Take), (9, PokerKind.Move), (2, PokerKind.Give), (2, PokerKind.Release), (2, PokerKind.Delete),
        (1, PokerKind.ClearAll), (2, PokerKind.CommitPending), (1, PokerKind.DiscardPending),
    ];

    private static readonly int PokerWeight = PokerMix.Sum(x => x.Weight);

    private enum PokerKind : byte {
        Vote,
        Lock,
        Abstain,
        Reveal,
        Replay,
        Create,
        CreateMany,
        Next,
        End,
        Activate,
        SetTicket,
        Reorder,
        Settings,
        Observer,
        Take,
        Move,
        Give,
        Release,
        Delete,
        ClearAll,
        CommitPending,
        DiscardPending,
    }

    public static Command? Poker(World world, SimTab tab, RoomClient room) {
        if (room.Poker is not { } model) {
            return null;
        }
        var rng = world.Rng;
        var (snapshot, _) = model.View(room.Presence);
        if (snapshot.Rounds.Count == 0) {
            return new PokerRoundCreate(new RoundId(world.NewGuid()));
        }
        var round = model.Viewed is { } viewed && rng.Next(10) < 7 && snapshot.Rounds.FirstOrDefault(x => x.Id == viewed) is { } shown
            ? shown
            : snapshot.Rounds[rng.Next(snapshot.Rounds.Count)];
        var me = tab.Who.Id;
        var points = Deck.Values[rng.Next(Deck.Values.Length)];
        return Pick(rng) switch {
            PokerKind.Vote => new PokerVoteSet(round.Id, points, rng.Next(5) == 0 ? Deck.Next(points) : null, rng.Next(3) == 0 ? true : null),
            PokerKind.Lock => new PokerLockSet(round.Id, rng.Next(4) != 0),
            PokerKind.Abstain => new PokerAbstainSet(round.Id, rng.Next(3) != 0),
            PokerKind.Reveal => new PokerRoundReveal(round.Id),
            PokerKind.Replay => new PokerRoundReplay(round.Id),
            PokerKind.Create => new PokerRoundCreate(
                new RoundId(world.NewGuid()),
                Ticket(rng),
                snapshot.Spotlight?.Holder == me && rng.Next(2) == 0 ? true : null,
                rng.Next(3) == 0 ? true : null),
            PokerKind.CreateMany => new PokerRoundCreateMany([.. Enumerable.Range(0, rng.Next(1, 4)).Select(_ => new NewRound(new RoundId(world.NewGuid()), Ticket(rng)))]),
            PokerKind.Next => new PokerRoundNext(snapshot.CurrentRoundId ?? round.Id, new RoundId(world.NewGuid()), Ticket(rng)),
            PokerKind.End => new PokerRoundEnd(round.Id),
            PokerKind.Activate => new PokerRoundActivate(round.Id),
            // Mostly the version on screen; sometimes one behind, as a debounced edit would carry.
            PokerKind.SetTicket => new PokerRoundSetTicket(round.Id, Ticket(rng), rng.Next(8) == 0 ? round.Version - 1 : round.Version),
            PokerKind.Reorder => new PokerRoundReorder([.. snapshot.Rounds.Select(x => x.Id).OrderBy(_ => rng.Next())]),
            PokerKind.Settings => rng.Next(2) == 0
                ? new PokerSettingsUpdate(new PokerSettingsValues(PresenceEnabled: snapshot.PresenceEnabled), PresenceEnabled: !snapshot.PresenceEnabled)
                : new PokerSettingsUpdate(new PokerSettingsValues(FollowSpotlight: snapshot.FollowSpotlight), FollowSpotlight: !snapshot.FollowSpotlight),
            PokerKind.Observer => new PokerObserverSet(Someone(rng, snapshot) ?? me, rng.Next(3) == 0),
            PokerKind.Take => new PokerSpotlightTake(round.Id, rng.Next(2) == 0),
            PokerKind.Move => new PokerSpotlightMove(round.Id),
            PokerKind.Give => Someone(rng, snapshot) is { } target ? new PokerSpotlightGive(target) : null,
            PokerKind.Release => new PokerSpotlightRelease(),
            PokerKind.Delete => new PokerRoundDelete(round.Id),
            PokerKind.ClearAll => new PokerRoundsClearAll(new RoundId(world.NewGuid())),
            PokerKind.CommitPending => Pending(rng, snapshot, me) is { } pending ? new PokerRoundCommitPending(pending) : null,
            PokerKind.DiscardPending => Pending(rng, snapshot, me) is { } discard ? new PokerRoundDiscardPending(discard) : null,
            _ => null,
        };
    }

    public static Command? Team(World world, SimTab tab, RoomClient room) {
        if (room.Team is not { } model || model.Items.Count == 0) {
            return null;
        }
        var rng = world.Rng;
        var items = model.Items.OrderBy(x => x.Id.Value).ToList();
        var item = items[rng.Next(items.Count)];
        return rng.Next(10) switch {
            < 5 => new TeamActionAssign(item.Id, rng.Next(4) == 0 ? null : model.Members[rng.Next(model.Members.Count)].Pid),
            < 9 => new TeamActionDone(item.Id, rng.Next(3) != 0),
            _ => new TeamActionDelete(item.Id),
        };
    }

    private static PokerKind Pick(Random rng) {
        var roll = rng.Next(PokerWeight);
        foreach (var (weight, kind) in PokerMix) {
            if (roll < weight) {
                return kind;
            }
            roll -= weight;
        }
        return PokerKind.Vote;
    }

    private static string? Ticket(Random rng) => rng.Next(4) == 0 ? null : $"SIM-{rng.Next(1, 40)}";

    private static PrincipalId? Someone(Random rng, PokerSnapshot snapshot) {
        var people = snapshot.Presence.Count > 0 ? snapshot.Presence : snapshot.Roster;
        return people.Count == 0 ? null : people[rng.Next(people.Count)].Pid;
    }

    private static RoundId? Pending(Random rng, PokerSnapshot snapshot, PrincipalId me) {
        var pending = snapshot.Rounds.Where(x => x.Pending && (x.PendingOwner == me || rng.Next(4) == 0)).ToList();
        return pending.Count == 0 ? null : pending[rng.Next(pending.Count)].Id;
    }
}
