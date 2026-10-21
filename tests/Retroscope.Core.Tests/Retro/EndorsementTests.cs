using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using Retroscope.Core.Room;
using static Retroscope.Core.Tests.Retro.RetroFixture;

namespace Retroscope.Core.Tests.Retro;

[Trait("Category", "Retro")]
public sealed class EndorsementTests {
    [Fact]
    public void Members_endorse_other_members_once_per_type() {
        var fx = new RetroFixture(team: true);
        var id = fx.NewEndorsementId();
        var added = Assert.IsType<EndorsementAdded>(Assert.Single(fx.Do(Alice, new RetroEndorseGive(id, Bob.Id, Helper))));
        Assert.Equal(new EndorsementInfo(id, Helper, Alice.Id, Bob.Id, Start.ToUnixTimeMilliseconds()), added.Endorsement);

        fx.NoOp(Alice, new RetroEndorseGive(fx.NewEndorsementId(), Bob.Id, Helper));
        fx.Rejects(Alice, new RetroEndorseGive(id, Bob.Id, Helper), NackCode.Invalid);
    }

    [Fact]
    public void Who_may_give_and_receive() {
        var fx = new RetroFixture(team: true);
        fx.Rejects(Carol, new RetroEndorseGive(fx.NewEndorsementId(), Bob.Id, Helper), NackCode.Forbidden);
        fx.Rejects(Gus, new RetroEndorseGive(fx.NewEndorsementId(), Bob.Id, Helper), NackCode.Forbidden);
        fx.Rejects(Alice, new RetroEndorseGive(fx.NewEndorsementId(), Carol.Id, Helper), NackCode.Invalid);
        fx.Rejects(Alice, new RetroEndorseGive(fx.NewEndorsementId(), Alice.Id, Helper), NackCode.Invalid);
        fx.Rejects(Alice, new RetroEndorseGive(fx.NewEndorsementId(), Bob.Id, Retired), NackCode.NotFound);
        fx.Rejects(Alice, new RetroEndorseGive(fx.NewEndorsementId(), Bob.Id, new EndorsementTypeId(Guid.NewGuid())), NackCode.NotFound);

        var quick = new RetroFixture();
        quick.Rejects(Alice, new RetroEndorseGive(quick.NewEndorsementId(), Bob.Id, Helper), NackCode.Invalid);
    }

    [Fact]
    public void The_per_board_limit_is_live_from_team_settings() {
        var fx = new RetroFixture(team: true);
        var dave = User("d4000000-0000-4000-8000-000000000005", "Dave");
        fx.Access.Roles[(Team, dave.Id)] = TeamRole.Member;
        fx.State.EndorsementTypes.Add(new EndorsementTypeInfo(new EndorsementTypeId(Guid.NewGuid()), "Spark", "✨", false));
        var types = fx.State.EndorsementTypes.Where(t => !t.Archived).Select(t => t.Id).ToList();
        fx.Do(null, new RetroInput.TeamSettingsChanged(2));

        fx.Do(Alice, new RetroEndorseGive(fx.NewEndorsementId(), Bob.Id, types[0]));
        fx.Do(Alice, new RetroEndorseGive(fx.NewEndorsementId(), dave.Id, types[0]));
        fx.Rejects(Alice, new RetroEndorseGive(fx.NewEndorsementId(), Bob.Id, types[1]), NackCode.EndorseLimit);
        fx.Do(Bob, new RetroEndorseGive(fx.NewEndorsementId(), Alice.Id, types[1]));

        fx.Do(null, new RetroInput.TeamSettingsChanged(3));
        fx.Do(Alice, new RetroEndorseGive(fx.NewEndorsementId(), Bob.Id, types[1]));
        fx.NoOp(null, new RetroInput.TeamSettingsChanged(3));
    }

    [Fact]
    public void Only_the_giver_revokes() {
        var fx = new RetroFixture(team: true);
        var id = fx.NewEndorsementId();
        fx.Do(Alice, new RetroEndorseGive(id, Bob.Id, Helper));

        fx.Rejects(Bob, new RetroEndorseRevoke(id), NackCode.Forbidden);
        Assert.Equal(new EndorsementRemoved(id), Assert.Single(fx.Do(Alice, new RetroEndorseRevoke(id))));
        fx.Rejects(Alice, new RetroEndorseRevoke(id), NackCode.NotFound);
    }
}
