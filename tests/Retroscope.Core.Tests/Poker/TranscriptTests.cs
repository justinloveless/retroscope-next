using System.Text;
using Retroscope.Core.Ids;
using Retroscope.Core.Poker;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Core.Tests.Scaffold;
using static Retroscope.Core.Tests.Poker.PokerHarness;

namespace Retroscope.Core.Tests.Poker;

/// <summary>
/// Golden transcripts (§g.4): what one viewer receives over a scenario, the navigation each frame causes and the
/// state the client must end in. <c>tests/web-unit/reducers.test.js</c> replays them through <c>poker/model.js</c>.
/// A stale file is regenerated and the test fails, so review and commit the new file.
/// </summary>
[Trait("Category", "Poker")]
public sealed class TranscriptTests {
    private static readonly Epoch TranscriptEpoch = new(0x0123456789abcdef);

    [Fact]
    public void Votes_reveal_and_replay() {
        var room = PokerHarness.Team();
        JoinAll(room, Alice, Bob, Carol, Dave);
        var transcript = new Recorder("poker-votes-reveal-replay", "Own values versus others' status, abstain, reveal, replay.", room, Carol);

        room.Accepted(Bob, new PokerVoteSet(R(1), 5));
        room.Accepted(Carol, new PokerVoteSet(R(1), 3, High: 5));
        room.Accepted(Bob, new PokerLockSet(R(1), true));
        room.Accepted(Carol, new PokerAbstainSet(R(1), true));
        room.Accepted(Carol, new PokerAbstainSet(R(1), false));
        room.Accepted(Carol, new PokerVoteSet(R(1), 8, Lock: true));
        room.Accepted(Dave, new PokerVoteSet(R(1), 13));
        room.Accepted(Alice, new PokerRoundReveal(R(1)));
        room.Accepted(Dave, new PokerObserverSet(Dave.Who.Id, true));
        room.Accepted(Bob, new PokerRoundReplay(R(1)));
        room.Accepted(Alice, new PokerVoteSet(R(1), 2, Lock: true));
        room.Accepted(Alice, new PokerRoundReveal(R(1)));

        transcript.Finish();
    }

    [Fact]
    public void Auto_reveal_jira_and_team_settings() {
        var room = PokerHarness.Team();
        JoinAll(room, Bob, Carol);
        var transcript = new Recorder("poker-auto-reveal-jira", "Server auto-reveal, Jira submit states, parent keys, roster and point descriptions.", room, Bob);

        room.Accepted(Bob, new PokerRoundSetTicket(R(1), "42", 1));
        room.Accepted(null, new JiraMetaLoaded([new JiraMeta("ABC-42", "Hidden title", ParentKey: "ABC-7")]));
        room.Accepted(Bob, new PokerVoteSet(R(1), 5, Lock: true));
        room.Accepted(Carol, new PokerVoteSet(R(1), 8, Lock: true));
        room.Advance(TimeSpan.FromSeconds(1));
        room.Accepted(Carol, new PokerJiraSubmitPoints(R(1), 8));
        room.Accepted(null, new JiraPointsResult(R(1), "ABC-42", 8, Carol.Who.Id, false, "jira_forbidden"));
        room.Accepted(Bob, new PokerJiraSubmitPoints(R(1), 5));
        room.Advance(TimeSpan.FromSeconds(2));
        room.Accepted(null, new JiraPointsResult(R(1), "ABC-42", 5, Bob.Who.Id, true));
        room.Accepted(null, new RosterChanged([Info(Alice), Info(Bob), Info(Carol)]));
        room.Accepted(null, new PointDescriptionsChanged(new Dictionary<string, string> { ["1"] = "an hour", ["8"] = "a week" }));
        room.Accepted(Bob, new PokerSettingsUpdate(new PokerSettingsValues(PresenceEnabled: true), PresenceEnabled: false));

        transcript.Finish();
    }

    [Fact]
    public void Pointer_navigation_and_spotlight_follow() {
        var room = PokerHarness.Team();
        JoinAll(room, Alice, Bob, Carol, Dave);
        room.Accepted(Dave, new PokerSpotlightTake(R(1), false));
        var transcript = new Recorder("poker-pointer-nav", "nav:true moves followers too; spotlight changes move followers; repairs move only viewers of a vanished round.", room, Carol);

        room.Accepted(Bob, new PokerRoundCreate(R(2), "ABC-2", ForceNew: true));
        room.Accepted(Dave, new PokerSpotlightMove(R(2)));
        room.Accepted(Dave, new PokerSpotlightMove(R(1)));
        room.Accepted(Bob, new PokerRoundCreateMany([new NewRound(R(3), "ABC-3"), new NewRound(R(4), "ABC-4")]));
        room.Accepted(Dave, new PokerRoundNext(R(4), R(5), "ABC-5"));
        room.Accepted(Bob, new PokerRoundReorder([R(5), R(1), R(2), R(3), R(4)]));
        room.Accepted(Bob, new PokerRoundActivate(R(2)));
        room.Accepted(Bob, new PokerRoundEnd(R(2)));
        room.Accepted(Bob, new PokerRoundSetTicket(R(5), "ABC-50", 1));
        room.Accepted(Alice, new PokerRoundDelete(R(5)));
        room.Accepted(Alice, new PokerRoundDelete(R(1)));
        room.Accepted(Alice, new PokerRoundsClearAll(R(6)));

        transcript.Finish();
    }

    [Fact]
    public void Pending_preview_commit_and_discard() {
        var room = PokerHarness.Team();
        JoinAll(room, Alice, Bob, Carol, Dave);
        room.Accepted(Dave, new PokerSpotlightTake(R(1), false));
        var transcript = new Recorder("poker-pending-preview", "A preview moves the pointer without navigation and followers see it through the spotlight.", room, Carol);

        room.Accepted(Dave, new PokerRoundCreate(R(2), "ABC-2", Pending: true, ForceNew: true));
        room.Accepted(Dave, new PokerRoundCommitPending(R(2)));
        room.Accepted(Dave, new PokerRoundCreate(R(3), "ABC-3", Pending: true));
        room.Accepted(Dave, new PokerRoundDiscardPending(R(3)));
        room.Accepted(Bob, new PokerSettingsUpdate(new PokerSettingsValues(FollowSpotlight: true), FollowSpotlight: false));
        room.Accepted(Dave, new PokerSpotlightMove(R(1)));
        room.Accepted(Dave, new PokerRoundCreate(R(4), "ABC-4", Pending: true));
        room.Accepted(Bob, new PokerRoundNext(R(2), R(5)));
        room.Accepted(Bob, new PokerSettingsUpdate(new PokerSettingsValues(FollowSpotlight: false), FollowSpotlight: true));
        room.Accepted(Carol, new PokerSpotlightTake(R(1), true));
        room.Accepted(Carol, new PokerSpotlightGive(Dave.Who.Id));
        room.Accepted(Dave, new PokerSpotlightRelease());

        transcript.Finish();
    }

    [Fact]
    public void Quick_session_as_a_guest() {
        var room = Quick();
        JoinAll(room, Alice, Guest, Outsider);
        var transcript = new Recorder("poker-quick-guest", "Voters are seated on their first vote; observers; the session is deleted.", room, Guest);

        room.Accepted(Guest, new PokerVoteSet(R(1), 2, Lock: true));
        room.Accepted(Outsider, new PokerVoteSet(R(1), 21));
        room.Accepted(Alice, new PokerObserverSet(Alice.Who.Id, true));
        room.Accepted(Outsider, new PokerRoundReveal(R(1)));
        room.Accepted(Guest, new PokerRoundNext(R(1), R(2)));
        room.Accepted(Outsider, new PokerSpotlightTake(R(2), false));
        room.Accepted(Alice, new DeleteSession());

        transcript.Finish();
    }

    private static void JoinAll(PokerHarness room, params Viewer[] viewers) {
        foreach (var viewer in viewers) {
            room.Join(viewer);
        }
    }

    /// <summary>Records one viewer's frames: a snap at seq 0, then a tx frame per transition that reaches the wire.</summary>
    private sealed class Recorder {
        private readonly string _name;
        private readonly string _description;
        private readonly PokerHarness _room;
        private readonly Viewer _viewer;
        private readonly ViewerModel _model;
        private readonly List<string> _frames = [];
        private readonly List<RoundId?> _nav = [];
        private long _seq;

        public Recorder(string name, string description, PokerHarness room, Viewer viewer) {
            _name = name;
            _description = description;
            _room = room;
            _viewer = viewer;
            var snapshot = PokerProjection.Snapshot(room.State, room.PresenceInfo());
            var overlay = PokerProjection.Overlay(room.State, viewer.Who.Id, viewer.Client);
            _frames.Add(Json(new SnapFrame(Room, TranscriptEpoch, 0, 0, snapshot, overlay)));
            _model = new ViewerModel(snapshot, overlay, viewer.Who.Id);
            _nav.Add(_model.Viewed);
            room.Applied = OnApplied;
        }

        private static RoomKey Room => RoomKey.Poker(Session);

        public void Finish() {
            _room.Applied = null;
            var expected = PokerPropertyTests.Snap(_room, _viewer);
            var (snapshot, overlay) = _model.View(_room.PresenceInfo());
            Assert.Equal(expected, PokerPropertyTests.WireJson(new SnapFrame(Room, default, 0, 0, snapshot, overlay)));

            var finalSnap = new SnapFrame(
                Room,
                TranscriptEpoch,
                _seq,
                0,
                PokerProjection.Snapshot(_room.State, _room.PresenceInfo()),
                PokerProjection.Overlay(_room.State, _viewer.Who.Id, _viewer.Client));
            using var final = System.Text.Json.JsonDocument.Parse(Json(finalSnap));
            var text = new StringBuilder();
            text.Append("{\n");
            text.Append($"  \"name\": \"{_name}\",\n");
            text.Append($"  \"description\": \"{_description}\",\n");
            text.Append($"  \"viewer\": {{\"pid\": \"{_viewer.Who.Id}\", \"cid\": \"{_viewer.Client}\"}},\n");
            text.Append("  \"frames\": [\n");
            text.AppendJoin(",\n", _frames.Select(x => "    " + x)).Append("\n  ],\n");
            text.Append("  \"expectedNav\": [");
            text.AppendJoin(", ", _nav.Select(x => x is { } id ? $"\"{id}\"" : "null")).Append("],\n");
            text.Append("  \"expectedViewerState\": {\n");
            text.Append($"    \"d\": {final.RootElement.GetProperty("d").GetRawText()},\n");
            text.Append($"    \"o\": {final.RootElement.GetProperty("o").GetRawText()}\n");
            text.Append("  }\n}\n");
            AssertCurrent($"tests/golden/transcripts/{_name}.json", text.ToString());
        }

        private void OnApplied(IReadOnlyList<PokerEvent> events) {
            if (events.All(x => x.Durability == EventDurability.MemoryOnly)) {
                return;
            }
            var wire = PokerProjection.Events(events, _viewer.Who.Id);
            _seq++;
            _frames.Add(Json(new TxFrame(Room, _seq, wire)));
            _nav.Add(_model.Apply(wire));
        }

        private static string Json(ServerFrame frame) => PokerPropertyTests.WireJson(frame);

        private static void AssertCurrent(string relativePath, string generated) {
            var path = Path.Combine(RepoRoot.Path, relativePath);
            var committed = File.Exists(path) ? File.ReadAllText(path) : null;
            if (committed == generated) {
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, generated);
            Assert.Fail($"{relativePath} was stale and has been regenerated; review and commit it.");
        }
    }
}
