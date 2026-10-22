using System.Text.Json;

namespace Retroscope.Soak;

/// <summary>
/// Proves the oracle has teeth before any run trusts it: a clean synthetic run passes, and each planted bug (a lost
/// acked card, a phantom card, a stale overwrite, a lost vote, a double reveal, a double or missing timer expiry, a lost dup-acked
/// round, a command never answered) is reported under its own gate.
/// </summary>
internal static class SelfTest {
    private static readonly Guid Session = Guid.Parse("00000000-0000-4000-8000-0000000000a1");
    private static readonly Guid Board = Guid.Parse("00000000-0000-4000-8000-0000000000b1");
    private static readonly Guid Ann = Guid.Parse("00000000-0000-4000-8000-0000000000c1");
    private static readonly Guid Bob = Guid.Parse("00000000-0000-4000-8000-0000000000c2");
    private static readonly Guid Round1 = Guid.Parse("00000000-0000-4000-8000-0000000000d1");
    private static readonly Guid Round2 = Guid.Parse("00000000-0000-4000-8000-0000000000d2");
    private static readonly Guid Card = Guid.Parse("00000000-0000-4000-8000-0000000000e1");
    private static readonly DateTimeOffset End = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    public static IReadOnlyList<string> Run() {
        var failures = new List<string>();
        var clean = Oracle.Check(Results(), CleanRows(), [], End);
        if (clean.Count > 0) {
            failures.Add($"a clean run was flagged: {string.Join("; ", clean.Select(f => $"{f.Gate}: {f.Detail}"))}");
        }
        Expect(failures, "lost_acked", Results(), CleanRows() with { Cards = [] });
        Expect(failures, "duplicate", Results(), CleanRows() with {
            Cards = [.. CleanRows().Cards, new CardRow(Guid.NewGuid(), Board, "phantom", 1)],
        });
        Expect(failures, "stale_overwrite", Results(extraBobEdit: Intent(9, "retro.card.edit", "ack", new { cardId = Card, body = "b", baseVersion = 1 })),
            CleanRows() with { Cards = [new CardRow(Card, Board, "b", 3)] });
        Expect(failures, "wrong_state", Results(), CleanRows() with { Votes = [new PokerVoteRow(Round1, Ann, 3, false, false)] });
        Expect(failures, "reveal_twice", Results(reveals: 2), CleanRows());
        Expect(failures, "timer_expired_twice", Results(expired: 2), CleanRows());
        Expect(failures, "timer_missing", Results(expired: 0), CleanRows());
        var endedInSnap = Oracle.Check(Results(expired: 0, endedInSnap: 1), CleanRows(), [], End);
        if (endedInSnap.Count > 0) {
            failures.Add($"a timer that a resuming tab saw ended in its snapshot was flagged: {string.Join("; ", endedInSnap.Select(f => f.Gate))}");
        }
        Expect(failures, "lost_acked", Results(), CleanRows() with { Rounds = [CleanRows().Rounds[0]] });
        Expect(failures, "unacked", Results(extraBobEdit: Intent(9, "retro.vote.set", "pending", new { cardId = Card, on = true })), CleanRows());
        return failures;
    }

    private static void Expect(List<string> failures, string gate, ClientResults results, Rows rows) {
        var findings = Oracle.Check(results, rows, [], End);
        if (!findings.Any(f => f.Gate == gate)) {
            failures.Add($"a planted {gate} was not caught (found: {string.Join(", ", findings.Select(f => f.Gate))})");
        }
    }

    private static Rows CleanRows() => new(
        [
            new PokerRoundRow(Round1, Session, "revealed", null, End - TimeSpan.FromMinutes(5)),
            new PokerRoundRow(Round2, Session, "voting", "SOAK-1", null),
        ],
        [new PokerVoteRow(Round1, Ann, 5, true, false), new PokerVoteRow(Round1, Bob, null, true, true)],
        [new CardRow(Card, Board, "edited", 2)],
        [],
        [new CardVoteRow(Card, Bob)]);

    private static ClientResults Results(int reveals = 1, int expired = 1, int endedInSnap = 0, IntentResult? extraBobEdit = null) {
        var poker = new RoomResult($"poker:{Session}", "poker", "poker", Session, [Round1],
            new Dictionary<Guid, int> { [Round1] = reveals }, []);
        var board = new RoomResult($"board:{Board}", "board", "retro", Board, [],
            new Dictionary<Guid, int>(), [new TimerResult(End.AddMinutes(-3).ToUnixTimeMilliseconds(), null, expired, endedInSnap)]);
        var ann = new List<IntentResult> {
            Intent(1, "poker.vote.set", "ack", new { roundId = Round1, points = 5, @lock = true }, s: 3),
            Intent(2, "poker.round.next", "dup", new { fromRoundId = Round1, newRoundId = Round2, ticketKey = "SOAK-1" }),
            Intent(3, "retro.card.add", "ack", new { cardId = Card, columnId = Guid.Empty, body = "first", anonymous = false }),
            Intent(4, "retro.card.edit", "ack", new { cardId = Card, body = "edited", baseVersion = 1 }),
        };
        var bob = new List<IntentResult> {
            Intent(1, "poker.vote.set", "ack", new { roundId = Round1, points = 8 }, s: 2),
            Intent(2, "retro.vote.set", "ack", new { cardId = Card, on = true }, s: 5),
            Intent(3, "retro.card.edit", "nack", new { cardId = Card, body = "late", baseVersion = 1 }) with { Code = "stale" },
        };
        if (extraBobEdit is not null) {
            bob.Add(extraBobEdit);
        }
        return new ClientResults(
            [poker, board],
            [
                new TabResult(poker.Key, "ann", Ann, false, ann.Where(i => i.K.StartsWith("poker", StringComparison.Ordinal)).ToList()),
                new TabResult(board.Key, "ann", Ann, false, ann.Where(i => i.K.StartsWith("retro", StringComparison.Ordinal)).ToList()),
                new TabResult(poker.Key, "bob", Bob, false, bob.Where(i => i.K.StartsWith("poker", StringComparison.Ordinal)).ToList()),
                new TabResult(board.Key, "bob", Bob, false, bob.Where(i => i.K.StartsWith("retro", StringComparison.Ordinal)).ToList()),
            ],
            new LatencyResult(0, 0, 0, 0, 0),
            new Dictionary<string, int>(),
            [],
            []);
    }

    private static IntentResult Intent(int id, string kind, string via, object data, long? s = null) =>
        new(id, kind, JsonSerializer.SerializeToElement(data, ClientResults.Json), via, S: s, Order: via == "ack" ? id : null);
}
