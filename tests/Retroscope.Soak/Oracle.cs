namespace Retroscope.Soak;

/// <summary>One broken §j guarantee. <see cref="Gate"/> names the guarantee, so the report can count per gate.</summary>
internal sealed record Finding(string Gate, string Detail);

/// <summary>A span in which the app was down or restarting: frames from it may be missing from every tab (new epoch).</summary>
internal sealed record Outage(string Name, DateTimeOffset From, DateTimeOffset To) {
    public bool Covers(DateTimeOffset instant) => instant >= From - TimeSpan.FromSeconds(2) && instant <= To + TimeSpan.FromSeconds(10);
}

/// <summary>
/// Compares what the clients were told with what the database holds after the run (DESIGN §j): no lost acked command,
/// no duplicate entity, no stale overwrite, one reveal and one timer expiry per transition, nothing left unanswered.
/// An <c>ack</c> is a certain application. A <c>dup</c> ack (or a resume cursor past the command) is almost always one,
/// but a final nack lost with its socket is also answered <c>dup</c> once a later command moved the cursor (§c.7 step 1),
/// so where the final state depends on such an intent every outcome it allows is accepted.
/// </summary>
internal static class Oracle {
    public static IReadOnlyList<Finding> Check(ClientResults results, Rows rows, IReadOnlyList<Outage> outages, DateTimeOffset runEnd) {
        var findings = new List<Finding>();
        foreach (var tab in results.Tabs) {
            foreach (var intent in tab.Intents.Where(i => i.Via == "pending")) {
                findings.Add(new("unacked", $"{tab.Label} {intent.K} #{intent.Intent} was never answered"));
            }
        }
        foreach (var violation in results.Violations) {
            findings.Add(new(violation.GetProperty("type").GetString()!, violation.ToString()));
        }
        CheckPoker(results, rows, outages, findings);
        CheckBoards(results, rows, findings);
        CheckTimers(results, outages, runEnd, findings);
        return findings;
    }

    private static void CheckPoker(ClientResults results, Rows rows, IReadOnlyList<Outage> outages, List<Finding> findings) {
        var rooms = results.Rooms.Where(r => r.Kind == "poker").ToDictionary(r => r.Key);
        var intents = IntentsOf(results, rooms.Keys);
        var created = new Dictionary<Guid, IntentResult>();
        foreach (var intent in intents.Where(i => i.Intent.K is "poker.round.create" or "poker.round.next")) {
            var member = intent.Intent.K == "poker.round.next" ? "newRoundId" : "roundId";
            created[intent.Intent.Id(member)] = intent.Intent;
        }
        var initial = rooms.Values.SelectMany(r => r.InitialRounds ?? []).ToHashSet();
        var roundsById = rows.Rounds.ToDictionary(r => r.Id);

        ExpectCreated(created, roundsById.Keys.ToHashSet(), initial, "round", findings);

        foreach (var group in intents.Where(i => i.Intent.K == "poker.round.setTicket").GroupBy(i => i.Intent.Id("roundId"))) {
            var round = roundsById.GetValueOrDefault(group.Key);
            var ticketAtCreation = created.TryGetValue(group.Key, out var create) && create.D.TryGetProperty("ticketKey", out var key)
                ? key.GetString()
                : null;
            CheckOverwrites(
                $"round {group.Key} ticket", group.Select(i => i.Intent).ToList(), "baseVersion", "ticketKey",
                ticketAtCreation, round?.TicketKey, findings);
        }

        var votes = rows.Votes.ToDictionary(v => (v.RoundId, v.PrincipalId));
        var expectedVoters = new HashSet<(Guid, Guid)>();
        foreach (var group in intents.Where(i => i.Intent.K == "poker.vote.set" && i.Pid is not null)
                     .GroupBy(i => (Round: i.Intent.Id("roundId"), Pid: i.Pid!.Value))) {
            var ops = group.Select(i => i.Intent).Where(i => i.MaybeApplied).ToList();
            if (ops.Count == 0) {
                continue;
            }
            expectedVoters.Add(group.Key);
            var revealed = roundsById.GetValueOrDefault(group.Key.Round)?.State == "revealed";
            var allowed = Allowed(ops, op => VoteOutcome(op, revealed), absent: "none");
            var actual = votes.TryGetValue(group.Key, out var row) ? Describe(row) : "none";
            if (!allowed.Contains(actual)) {
                findings.Add(new(
                    ops.Any(o => o.Certain) && actual == "none" ? "lost_acked" : "wrong_state",
                    $"vote of {group.Key.Pid} on round {group.Key.Round} is {actual}, expected one of {string.Join(" | ", allowed)}"));
            }
        }
        foreach (var vote in rows.Votes.Where(v => !expectedVoters.Contains((v.RoundId, v.PrincipalId)))) {
            findings.Add(new("duplicate", $"vote of {vote.PrincipalId} on round {vote.RoundId} that no applied command made"));
        }

        foreach (var room in rooms.Values) {
            foreach (var (round, count) in room.Reveals.Where(r => r.Value > 1)) {
                findings.Add(new("reveal_twice", $"round {round} was revealed in {count} frames"));
            }
        }
        var observed = rooms.Values.SelectMany(r => r.Reveals.Keys).ToHashSet();
        foreach (var round in rows.Rounds.Where(r => r.State == "revealed" && !observed.Contains(r.Id))) {
            if (round.RevealedAt is { } at && !outages.Any(o => o.Covers(at))) {
                findings.Add(new("reveal_missing", $"round {round.Id} was revealed at {at:O} but no tab saw it"));
            }
        }
    }

    private static void CheckBoards(ClientResults results, Rows rows, List<Finding> findings) {
        var rooms = results.Rooms.Where(r => r.Kind == "board").Select(r => r.Key).ToHashSet();
        var intents = IntentsOf(results, rooms);
        var cardsById = rows.Cards.ToDictionary(c => c.Id);
        var adds = intents.Where(i => i.Intent.K == "retro.card.add").ToDictionary(i => i.Intent.Id("cardId"), i => i.Intent);
        ExpectCreated(adds, cardsById.Keys.ToHashSet(), [], "card", findings);
        var comments = intents.Where(i => i.Intent.K == "retro.comment.add").ToDictionary(i => i.Intent.Id("commentId"), i => i.Intent);
        ExpectCreated(comments, rows.Comments.Select(c => c.Id).ToHashSet(), [], "comment", findings);

        foreach (var group in intents.Where(i => i.Intent.K == "retro.card.edit").GroupBy(i => i.Intent.Id("cardId"))) {
            var edits = group.Select(i => i.Intent).ToList();
            var card = cardsById.GetValueOrDefault(group.Key);
            var original = adds.TryGetValue(group.Key, out var add) ? add.Text("body") : null;
            CheckOverwrites($"card {group.Key} body", edits, "baseVersion", "body", original, card?.Body, findings);
            if (card is not null) {
                var certain = edits.Count(e => e.Certain);
                var uncertain = edits.Count(e => e.Uncertain);
                if (card.Version < 1 + certain || card.Version > 1 + certain + uncertain) {
                    findings.Add(new(
                        certain >= card.Version ? "lost_acked" : "duplicate",
                        $"card {card.Id} is at version {card.Version} after {certain} acked and {uncertain} duplicate-acked edits"));
                }
            }
        }

        var voted = rows.CardVotes.Select(v => (v.CardId, v.VoterId)).ToHashSet();
        var expected = new HashSet<(Guid, Guid)>();
        foreach (var group in intents.Where(i => i.Intent.K == "retro.vote.set" && i.Pid is not null)
                     .GroupBy(i => (Card: i.Intent.Id("cardId"), Pid: i.Pid!.Value))) {
            var ops = group.Select(i => i.Intent).Where(i => i.MaybeApplied).ToList();
            if (ops.Count == 0) {
                continue;
            }
            expected.Add(group.Key);
            var allowed = Allowed(ops, op => op.D.GetProperty("on").GetBoolean() ? "on" : "off", absent: "off");
            var actual = voted.Contains(group.Key) ? "on" : "off";
            if (!allowed.Contains(actual)) {
                findings.Add(new("wrong_state", $"vote of {group.Key.Pid} on card {group.Key.Card} is {actual}, expected {string.Join(" | ", allowed)}"));
            }
        }
        foreach (var vote in voted.Where(v => !expected.Contains(v))) {
            findings.Add(new("duplicate", $"vote of {vote.Item2} on card {vote.Item1} that no applied command made"));
        }
    }

    private static void CheckTimers(ClientResults results, IReadOnlyList<Outage> outages, DateTimeOffset runEnd, List<Finding> findings) {
        foreach (var room in results.Rooms) {
            foreach (var timer in room.Timers) {
                var endsAt = DateTimeOffset.FromUnixTimeMilliseconds(timer.EndsAt);
                if (timer.Expired > 1) {
                    findings.Add(new("timer_expired_twice", $"{room.Key} timer ending {endsAt:O} expired in {timer.Expired} frames"));
                } else if (timer.Expired == 0 && endsAt < runEnd - TimeSpan.FromSeconds(10) && !outages.Any(o => o.Covers(endsAt))) {
                    findings.Add(new("timer_missing", $"{room.Key} timer ending {endsAt:O} never expired on any tab"));
                }
            }
        }
    }

    /// <summary>
    /// Overwrites with a precondition (<paramref name="baseMember"/>): two applied overwrites from the same base mean one
    /// was decided on a stale value; the final value must be the newest applied one (or one a dup-acked intent wrote).
    /// </summary>
    private static void CheckOverwrites(
        string what, IReadOnlyList<IntentResult> writes, string baseMember, string valueMember, string? original, string? actual,
        List<Finding> findings) {
        var certain = writes.Where(w => w.Certain).ToList();
        foreach (var clash in certain.GroupBy(w => w.D.GetProperty(baseMember).GetInt32()).Where(g => g.Count() > 1)) {
            findings.Add(new("stale_overwrite", $"{what}: {clash.Count()} writes were applied on base version {clash.Key}"));
        }
        string? Value(IntentResult w) => w.D.GetProperty(valueMember).GetString();
        var allowed = writes.Where(w => w.Uncertain).Select(Value).ToHashSet();
        if (certain.Count == 0) {
            allowed.Add(original);
        } else {
            allowed.Add(Value(certain.MaxBy(w => w.D.GetProperty(baseMember).GetInt32())!));
        }
        if (!allowed.Contains(actual)) {
            findings.Add(new(certain.Count > 0 ? "lost_acked" : "wrong_state", $"{what} is '{actual}', expected one of {allowed.Count} acked values"));
        }
    }

    /// <summary>Every created id that may have been applied exists; every row is one the clients created.</summary>
    private static void ExpectCreated(
        Dictionary<Guid, IntentResult> created, HashSet<Guid> rows, HashSet<Guid> initial, string what,
        List<Finding> findings) {
        foreach (var (id, intent) in created) {
            if (intent.MaybeApplied && !rows.Contains(id)) {
                findings.Add(new("lost_acked", $"{what} {id} ({intent.K}, {intent.Via}) is not in the database"));
            }
            if (!intent.MaybeApplied && rows.Contains(id)) {
                findings.Add(new("duplicate", $"{what} {id} exists although {intent.K} was answered {intent.Via} {intent.Code}"));
            }
        }
        foreach (var id in rows.Where(r => !created.ContainsKey(r) && !initial.Contains(r))) {
            findings.Add(new("duplicate", $"{what} {id} was created by no command"));
        }
    }

    /// <summary>
    /// The final states a sequence of set-semantics commands allows: the last certain one (by commit seq) or any dup-acked
    /// one; with no certain one, also <paramref name="absent"/>.
    /// </summary>
    private static HashSet<string> Allowed(IReadOnlyList<IntentResult> ops, Func<IntentResult, string> outcome, string absent) {
        var allowed = ops.Where(o => o.Uncertain).Select(outcome).ToHashSet();
        var last = ops.Where(o => o.Certain).OrderBy(o => o.S).ThenBy(o => o.Intent).LastOrDefault();
        allowed.Add(last is null ? absent : outcome(last));
        return allowed;
    }

    private static string VoteOutcome(IntentResult op, bool revealed) {
        var locked = op.D.TryGetProperty("lock", out var lockFlag) && lockFlag.GetBoolean();
        // A reveal turns every unlocked vote into a locked abstain (§c.4).
        return revealed && !locked ? "abstained" : $"{op.D.GetProperty("points").GetInt32()}{(locked ? " locked" : "")}";
    }

    private static string Describe(PokerVoteRow vote) =>
        vote.Abstained ? "abstained" : $"{vote.Points}{(vote.Locked ? " locked" : "")}";

    private static List<(IntentResult Intent, Guid? Pid)> IntentsOf(ClientResults results, IReadOnlyCollection<string> rooms) =>
        results.Tabs.Where(t => rooms.Contains(t.Room)).SelectMany(t => t.Intents.Select(i => (i, t.Pid))).ToList();
}
