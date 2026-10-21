using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Core.Tests.Protocol;

[Trait("Category", "Protocol")]
public sealed class WireRoundTripTests {
    private static readonly Epoch SomeEpoch = new(0x0123456789abcdefUL);
    private static readonly PrincipalId Alice = new(Guid.Parse("0192f0c1-0000-7000-8000-000000000001"));

    public static TheoryData<string, bool> Kinds() {
        var data = new TheoryData<string, bool>();
        foreach (var entry in Catalog.Entries) {
            data.Add(entry.Kind, true);
            data.Add(entry.Kind, false);
        }
        return data;
    }

    public static TheoryData<RoomKind> Rooms() => [RoomKind.Poker, RoomKind.Board, RoomKind.Team];

    [Theory]
    [MemberData(nameof(Kinds))]
    public void Every_kind_survives_its_frame_in_any_property_order(string kind, bool full) {
        var entry = Catalog.Entries.Single(x => x.Kind == kind);
        var sample = WireSamples.Json(entry.Type, full);
        var payload = WireSamples.Payload(entry.Type, full);

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(sample), JsonNode.Parse(JsonSerializer.Serialize(payload, entry.TypeInfo))));

        var room = new RoomKey(RoomOf(entry.Scope), Guid.CreateVersion7());
        switch (entry.Direction) {
            case KindDirection.Command:
                AssertClientRoundTrip(new CmdFrame(room, 1093, (Command)payload));
                break;
            case KindDirection.Event:
                AssertServerRoundTrip(new TxFrame(room, 44, [(WireEvent)payload, (WireEvent)payload]));
                break;
            case KindDirection.Unsequenced:
                AssertServerRoundTrip(new XFrame(room, (Unsequenced)payload));
                break;
        }
    }

    [Theory]
    [MemberData(nameof(Rooms))]
    public void Snapshots_and_overlays_survive_a_snap_frame(RoomKind kind) {
        var room = new RoomKey(kind, Guid.CreateVersion7());
        foreach (var full in new[] { true, false }) {
            var snapshot = (RoomSnapshot)WireSamples.Payload(Catalog.SnapshotTypeInfo(kind).Type, full);
            var overlayType = Catalog.OverlayTypeInfo(kind);
            var overlay = overlayType is null ? null : (RoomOverlay)WireSamples.Payload(overlayType.Type, full);

            AssertServerRoundTrip(new SnapFrame(room, SomeEpoch, 42, 1093, snapshot, overlay));
            AssertServerRoundTrip(new SnapFrame(room, SomeEpoch, 42, 1093, snapshot));
        }
    }

    [Fact]
    public void Replay_carries_events_of_several_kinds_per_tx() {
        var room = new RoomKey(RoomKind.Poker, Guid.CreateVersion7());
        var round = new RoundId(Guid.CreateVersion7());
        var replay = new ReplayFrame(room, SomeEpoch, 43, 1093, [
            new TxRecord(43, [new PokerVoteState(round, Alice, Status: VoteStatus.Locked), new PresenceLeave(Alice)]),
            new TxRecord(44, []),
            new TxRecord(45, [new PokerSpotlight()]),
        ]);

        AssertServerRoundTrip(replay);
        AssertServerRoundTrip(replay with { Txs = [] });
    }

    [Fact]
    public void Static_frames_round_trip() {
        var room = new RoomKey(RoomKind.Board, Guid.CreateVersion7());
        var cid = new ClientId(Guid.CreateVersion7());

        AssertClientRoundTrip(new HelloFrame(1, cid, [new SubCursor(room, SomeEpoch, 17), new SubCursor(room)]));
        AssertClientRoundTrip(new HelloFrame(1, cid, []));
        AssertClientRoundTrip(new SubFrame(room, SomeEpoch, 17));
        AssertClientRoundTrip(new SubFrame(room));
        AssertClientRoundTrip(new UnsubFrame(room));
        AssertClientRoundTrip(new PingFrame(1_760_000_000_123));

        var me = new PrincipalInfo(Alice, PrincipalKind.Guest, "Alice", new BlobId(Guid.CreateVersion7()));
        AssertServerRoundTrip(new WelcomeFrame(new ConnectionId(Guid.CreateVersion7()), me, 1_760_000_000_000));
        AssertServerRoundTrip(new AckFrame(room, 1093, 44));
        AssertServerRoundTrip(new AckFrame(room, 1093, 44, Dup: true));
        AssertServerRoundTrip(new NackFrame(room, 1093, NackCode.RoundRevealed, "Round already revealed"));
        AssertServerRoundTrip(new NackFrame(room, 1093, NackCode.ServerBusy));
        var current = JsonDocument.Parse("{\"current\":{\"ticketKey\":\"ABC-1\",\"version\":3}}").RootElement;
        AssertServerRoundTrip(new NackFrame(room, 1093, NackCode.Stale, D: current));
        AssertServerRoundTrip(new PongFrame(1_760_000_000_123, 1_760_000_000_150));
        AssertServerRoundTrip(new KickFrame(room, KickCode.AccessRevoked));
        AssertServerRoundTrip(new ByeFrame(ByeCode.Restart, 2600));
    }

    [Fact]
    public void Frames_use_the_documented_member_names() {
        var room = RoomKey.Poker(new SessionId(Guid.Parse("0192f0c1-0000-7000-8000-0000000000aa")));
        var round = new RoundId(Guid.Parse("0192f0c1-0000-7000-8000-0000000000bb"));

        var cmd = Encoding.UTF8.GetString(WireWriter.ToUtf8(new CmdFrame(room, 1093, new PokerVoteSet(round, 5, 8, true))));
        var nack = Encoding.UTF8.GetString(WireWriter.ToUtf8(new NackFrame(room, 7, NackCode.RateLimited)));

        Assert.Equal(
            "{\"t\":\"cmd\",\"r\":\"poker:0192f0c1-0000-7000-8000-0000000000aa\",\"q\":1093,\"k\":\"poker.vote.set\","
            + "\"d\":{\"roundId\":\"0192f0c1-0000-7000-8000-0000000000bb\",\"points\":5,\"high\":8,\"lock\":true}}",
            cmd);
        Assert.Equal("{\"t\":\"nack\",\"r\":\"poker:0192f0c1-0000-7000-8000-0000000000aa\",\"q\":7,\"code\":\"rate_limited\",\"retry\":true}", nack);
    }

    [Fact]
    public void Required_nullable_members_are_written_as_null() {
        var room = new RoomKey(RoomKind.Poker, Guid.CreateVersion7());
        var cmd = new CmdFrame(room, 1, new PokerRoundSetTicket(new RoundId(Guid.CreateVersion7()), null, 3));

        var json = Encoding.UTF8.GetString(WireWriter.ToUtf8(cmd));

        Assert.Contains("\"ticketKey\":null", json, StringComparison.Ordinal);
        AssertClientRoundTrip(cmd);
    }

    [Fact]
    public void Command_frames_hash_their_raw_data() {
        var room = new RoomKey(RoomKind.Board, Guid.CreateVersion7());
        var card = new CardId(Guid.CreateVersion7());
        var on = WireWriter.ToUtf8(new CmdFrame(room, 1, new RetroVoteSet(card, true)));
        var off = WireWriter.ToUtf8(new CmdFrame(room, 1, new RetroVoteSet(card, false)));

        var first = Assert.IsType<CmdFrame>(WireParser.ParseClient(on).Frame);
        var again = Assert.IsType<CmdFrame>(WireParser.ParseClient(on).Frame);
        var other = Assert.IsType<CmdFrame>(WireParser.ParseClient(off).Frame);

        Assert.NotEqual(0u, first.DataHash);
        Assert.Equal(first.DataHash, again.DataHash);
        Assert.NotEqual(first.DataHash, other.DataHash);
    }

    private static void AssertClientRoundTrip(ClientFrame frame) {
        var bytes = WireWriter.ToUtf8(frame);
        AssertSameAfterReparse(bytes, x => WireWriter.ToUtf8(Parsed(WireParser.ParseClient(x))));
    }

    private static void AssertServerRoundTrip(ServerFrame frame) {
        var bytes = WireWriter.ToUtf8(frame);
        AssertSameAfterReparse(bytes, x => WireWriter.ToUtf8(Parsed(WireParser.ParseServer(x))));
    }

    private static void AssertSameAfterReparse(byte[] bytes, Func<byte[], byte[]> reparse) {
        var canonical = Encoding.UTF8.GetString(bytes);
        Assert.Equal(canonical, Encoding.UTF8.GetString(reparse(bytes)));
        for (var seed = 1; seed <= 5; seed++) {
            var shuffled = Encoding.UTF8.GetBytes(WireSamples.Shuffle(canonical, seed));
            var reparsed = Encoding.UTF8.GetString(reparse(shuffled));
            Assert.True(JsonNode.DeepEquals(JsonNode.Parse(canonical), JsonNode.Parse(reparsed)), reparsed);
        }
    }

    private static T Parsed<T>(ParseResult<T> result) where T : class {
        Assert.True(result.Ok, $"{result.Error}: {result.Message}");
        return result.Frame!;
    }

    private static RoomKind RoomOf(RoomScope scope) {
        return scope.HasFlag(RoomScope.Poker) ? RoomKind.Poker
            : scope.HasFlag(RoomScope.Board) ? RoomKind.Board
            : RoomKind.Team;
    }
}
