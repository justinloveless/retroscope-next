using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using Retroscope.Core.Room;
using static Retroscope.Core.Tests.Retro.RetroFixture;

namespace Retroscope.Core.Tests.Retro;

/// <summary>
/// Seeded random command sequences from up to four principals, a non-member and a legacy guest among them: the board
/// invariants hold after every step, and every member's folded tx events equal a fresh snapshot.
/// </summary>
[Trait("Category", "Retro")]
public sealed class RandomSequenceTests {
    private static readonly Principal[] Cast = [Alice, Bob, Dave, Carol, Gus];
    private static readonly Principal[] Members = [Alice, Bob, Dave];

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void Invariants_hold_and_every_viewer_converges(int seed) {
        var random = new Random(seed);
        var driver = new RetroRoomDriver(new RetroFixture());
        var fx = driver.Fixture;
        foreach (var who in Members) {
            driver.Watch(who);
        }

        var accepted = 0;
        for (var step = 0; step < 1500; step++) {
            var who = Cast[random.Next(Cast.Length)];
            var input = NextInput(random, fx, who);
            if (driver.Run(input.Internal ? null : who, input.Input) is Decision<RetroEvent>.Accept) {
                accepted++;
            }
            AssertInvariants(fx.State);
            if (step % 50 == 0) {
                driver.AssertConverged();
            }
        }
        driver.AssertConverged();
        Assert.True(accepted > 300, $"only {accepted} inputs were accepted; the generator is too timid");
    }

    private static void AssertInvariants(RetroState state) {
        Assert.True(state.Columns.Count(c => c.ActionItems) <= 1);
        Assert.Equal(state.Columns.Count, state.Columns.Select(c => c.SortKey).Distinct().Count());
        Assert.All(state.Cards.Values, card => Assert.NotNull(state.FindColumn(card.ColumnId)));
        Assert.All(state.Comments.Values, comment => Assert.True(state.Cards.ContainsKey(comment.CardId)));
        Assert.True(state.Focus is null || state.Cards.ContainsKey(state.Focus.Value));
        Assert.All(state.Cards.Values, card => Assert.True(card.Version >= 1));
        Assert.True(state.Timer.State == TimerState.Running == state.Timer.EndsAt.HasValue);
        Assert.True(state.Timer.State == TimerState.Paused == state.Timer.RemainingMs.HasValue);
        Assert.All(
            state.Endorsements.Values.GroupBy(e => e.From),
            given => Assert.Equal(given.Count(), given.Select(e => (e.To, e.TypeId)).Distinct().Count()));
    }

    private readonly record struct Generated(RetroInput Input, bool Internal = false);

    private static Generated NextInput(Random random, RetroFixture fx, Principal who) {
        var state = fx.State;
        T Pick<T>(IReadOnlyList<T> items, Func<T> fallback) => items.Count == 0 || random.Next(10) == 0 ? fallback() : items[random.Next(items.Count)];
        CardId Card() => Pick(state.Cards.Keys.ToList(), fx.NewCardId);
        ColumnId ColumnOf() => Pick(state.Columns.Select(c => c.Id).ToList(), fx.NewColumnId);
        string Body() => random.Next(4) switch {
            0 => $"Hi [[mention:{Cast[random.Next(Cast.Length)].Id}:x]]",
            1 => $"![shot](blob:{Guid.NewGuid()})",
            2 => "**bold** and _italic_",
            _ => $"Card {random.Next(1000)}",
        };
        bool Coin() => random.Next(2) == 0;
        RetroStage Stage() => (RetroStage)random.Next(4);
        TimerState From() => random.Next(8) == 0 ? (TimerState)random.Next(3) : state.Timer.State;

        RetroCommand command = random.Next(26) switch {
            0 or 1 or 2 => new RetroCardAdd(fx.NewCardId(), ColumnOf(), Body(), random.Next(4) == 0),
            3 => EditCard(),
            4 => new RetroCardDelete(Card()),
            5 or 6 or 7 => new RetroVoteSet(Card(), random.Next(4) != 0),
            8 or 9 => new RetroCommentAdd(fx.NewCommentId(), Card(), Body(), random.Next(4) == 0),
            10 => new RetroCommentDelete(Pick(state.Comments.Keys.ToList(), fx.NewCommentId)),
            11 => new RetroColumnAdd(fx.NewColumnId(), $"Column {random.Next(100)}", (ColumnColor)random.Next(9)),
            12 => UpdateColumn(),
            13 => random.Next(4) == 0 ? new RetroColumnDelete(ColumnOf()) : new RetroColumnReorder(Shuffled()),
            14 => new RetroColumnSetActionItems(Coin() ? ColumnOf() : null),
            15 => new RetroBoardRename($"Retro {random.Next(100)}", state.Title),
            16 => Config(),
            17 or 18 => new RetroStageSet(random.Next(6) == 0 ? Stage() : state.Stage, Stage()),
            19 => new RetroReadySet(random.Next(6) == 0 ? Stage() : state.Stage, Coin()),
            20 => new RetroFocusSet(Coin() ? Card() : null, state.Focus),
            21 => random.Next(3) switch {
                0 => new RetroTimerStart(random.Next(1, 30) * 1000, Coin(), Coin(), From()),
                1 => new RetroTimerPause(From()),
                _ => new RetroTimerResume(From()),
            },
            22 => Coin() ? new RetroTimerReset(From()) : new RetroTimerSetFlags(Coin(), Coin()),
            23 => new RetroEndorseGive(fx.NewEndorsementId(), Cast[random.Next(Cast.Length)].Id, Coin() ? Helper : Retired),
            24 => new RetroEndorseRevoke(Pick(state.Endorsements.Keys.ToList(), fx.NewEndorsementId)),
            _ => default!,
        };
        if (command is not null) {
            return new Generated(command);
        }
        return random.Next(6) switch {
            0 => new Generated(new RetroInput.TimerDue(state.Timer.EndsAt ?? fx.Now), Internal: true),
            1 => new Generated(new RetroInput.TeamSettingsChanged(random.Next(0, 4)), Internal: true),
            2 => new Generated(new RetroInput.ArchiveSet(state.Archived ? true : random.Next(4) == 0, who.Id), Internal: true),
            3 => new Generated(new RetroInput.ArchiveSet(false, who.Id), Internal: true),
            _ => new Generated(new RetroInput.MembersChanged(Roster()), Internal: true),
        };

        List<MemberInfo> Roster() {
            var owner = new MemberInfo(Alice.Id, Alice.DisplayName, TeamRole.Owner);
            var dave = new MemberInfo(Dave.Id, Dave.DisplayName, TeamRole.Member);
            return [owner, new MemberInfo(Bob.Id, Bob.DisplayName, Coin() ? TeamRole.Member : TeamRole.Admin), dave];
        }

        RetroCommand EditCard() {
            var card = Card();
            var version = state.Cards.TryGetValue(card, out var current) ? current.Version : 1;
            return new RetroCardEdit(card, Body(), random.Next(8) == 0 ? version - 1 : version);
        }

        RetroCommand UpdateColumn() {
            var column = Pick(state.Columns, () => new Column { Id = fx.NewColumnId() });
            return new RetroColumnUpdate(column.Id, column.Version, Coin() ? $"Renamed {random.Next(100)}" : null, Coin() ? (ColumnColor)random.Next(9) : null);
        }

        List<ColumnId> Shuffled() {
            var order = state.Columns.Select(c => c.Id).ToArray();
            random.Shuffle(order);
            return [.. order];
        }

        RetroCommand Config() {
            var current = RetroConfigPatches.FromConfig(state.Config);
            var patch = random.Next(6) switch {
                0 => new RetroConfigPatch(StagesEnabled: Coin()),
                1 => new RetroConfigPatch(ShowAuthorNames: Coin()),
                2 => new RetroConfigPatch(MaxVotes: random.Next(0, 4)),
                3 => new RetroConfigPatch(AllowSelfVotes: Coin(), AllowAnonymous: Coin()),
                4 => new RetroConfigPatch(EnforceReadiness: Coin(), VotingEnabled: random.Next(5) != 0),
                _ => new RetroConfigPatch(VoteEmoji: Coin() ? "🔥" : "👍", SortChronologically: Coin()),
            };
            return new RetroConfigUpdate(patch, random.Next(8) == 0 ? new RetroConfigPatch() : current);
        }
    }
}
