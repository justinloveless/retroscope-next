using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using Retroscope.Core.Room;

namespace Retroscope.Core.Tests.Retro;

/// <summary>
/// Runs inputs the way the board actor does (decide, apply after "commit", project per viewer, one tx frame per
/// accepted input) and keeps, for each watching viewer, the frames it received and the model folded from them.
/// </summary>
internal sealed class RetroRoomDriver {
    public static readonly Epoch Epoch = new(0x5245_5452_4f00_0001);

    private readonly List<Watcher> _watchers = [];
    private long _seq;

    public RetroRoomDriver(RetroFixture fixture) {
        Fixture = fixture;
        Room = RoomKey.Board(fixture.State.BoardId);
    }

    public RetroFixture Fixture { get; }

    public RoomKey Room { get; }

    public long Seq => _seq;

    public IReadOnlyList<PrincipalInfo> PresenceInfo => Fixture.Present
        .Select(p => new PrincipalInfo(p.Id, p.Kind, p.DisplayName, p.AvatarBlobId))
        .ToList();

    /// <summary>Subscribes a viewer now: it gets a snapshot at the current seq.</summary>
    public Watcher Watch(Principal who) {
        var member = RetroRoles.IsMember(Fixture.State, who.Id, Fixture.Access);
        var snapshot = RetroProjection.Snapshot(Fixture.State, PresenceInfo, member);
        var overlay = Fixture.Projection.Overlay(Fixture.State, who.Id);
        var watcher = new Watcher(who, member, new RetroViewerModel(snapshot, overlay));
        watcher.Frames.Add(new SnapFrame(Room, Epoch, _seq, 0, snapshot, overlay));
        _watchers.Add(watcher);
        return watcher;
    }

    /// <summary>Decides and, when accepted, commits and fans out. Returns the decision.</summary>
    public Decision<RetroEvent> Run(Principal? who, RetroInput input) {
        var fx = Fixture;
        var decision = fx.Decide(who, input);
        if (decision is Decision<RetroEvent>.Accept accept) {
            foreach (var evt in accept.Events) {
                fx.Decider.Apply(fx.State, evt);
            }
            _seq++;
            foreach (var watcher in _watchers) {
                var wire = fx.Projection.ForViewer(fx.State, accept.Events, watcher.Who.Id, watcher.Member);
                watcher.Frames.Add(new TxFrame(Room, _seq, wire));
                foreach (var evt in wire) {
                    watcher.Model.Apply(evt);
                }
            }
        }
        fx.Now += TimeSpan.FromSeconds(1);
        return decision;
    }

    public void Accept(Principal? who, RetroInput input) => Assert.IsType<Decision<RetroEvent>.Accept>(Run(who, input));

    /// <summary>What a fresh subscription would get now, in the model's canonical form.</summary>
    public string Fresh(Watcher watcher) {
        return RetroViewerModel.Canonical(Snapshot(watcher), Fixture.Projection.Overlay(Fixture.State, watcher.Who.Id));
    }

    public RetroSnapshot Snapshot(Watcher watcher) => RetroProjection.Snapshot(Fixture.State, PresenceInfo, watcher.Member);

    public void AssertConverged() {
        foreach (var watcher in _watchers) {
            Assert.Equal(Fresh(watcher), watcher.Model.ToJson());
        }
    }

    internal sealed class Watcher(Principal who, bool member, RetroViewerModel model) {
        public Principal Who { get; } = who;

        public bool Member { get; } = member;

        public RetroViewerModel Model { get; } = model;

        public List<ServerFrame> Frames { get; } = [];
    }
}
