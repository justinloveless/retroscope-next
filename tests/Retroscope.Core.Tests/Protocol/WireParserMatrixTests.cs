using System.Text;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;

namespace Retroscope.Core.Tests.Protocol;

[Trait("Category", "Protocol")]
public sealed class WireParserMatrixTests {
    private const string Room = "poker:0192f0c1-0000-7000-8000-0000000000aa";
    private const string Round = "0192f0c1-0000-7000-8000-0000000000bb";

    [Theory]
    [InlineData("", FrameError.Malformed)]
    [InlineData("   ", FrameError.Malformed)]
    [InlineData("[]", FrameError.Malformed)]
    [InlineData("\"hello\"", FrameError.Malformed)]
    [InlineData("null", FrameError.Malformed)]
    [InlineData("{", FrameError.Malformed)]
    [InlineData("{\"t\":\"ping\",\"c\":1", FrameError.Malformed)]
    [InlineData("{\"t\":\"ping\",\"c\":1}{}", FrameError.Malformed)]
    [InlineData("{\"t\":\"ping\",\"c\":1} x", FrameError.Malformed)]
    [InlineData("{'t':'ping','c':1}", FrameError.Malformed)]
    [InlineData("{\"c\":1}", FrameError.Malformed)]
    [InlineData("{\"t\":7,\"c\":1}", FrameError.Malformed)]
    [InlineData("{\"t\":\"ping\",\"t\":\"ping\",\"c\":1}", FrameError.Malformed)]
    [InlineData("{\"t\":\"ping\",\"c\":1,\"zz\":1}", FrameError.Malformed)]
    [InlineData("{\"t\":\"ping\",\"c\":1,\"q\":1}", FrameError.Malformed)]
    [InlineData("{\"t\":\"ping\"}", FrameError.Malformed)]
    [InlineData("{\"t\":\"ping\",\"c\":\"1\"}", FrameError.Malformed)]
    [InlineData("{\"t\":\"ping\",\"c\":1.5}", FrameError.Malformed)]
    [InlineData("{\"t\":\"ping\",\"c\":null}", FrameError.Malformed)]
    [InlineData("{\"t\":\"PING\",\"c\":1}", FrameError.UnknownType)]
    [InlineData("{\"t\":\"welcome\",\"c\":1}", FrameError.UnknownType)]
    [InlineData("{\"t\":\"sub\",\"r\":\"poker:not-a-uuid\"}", FrameError.Malformed)]
    [InlineData("{\"t\":\"sub\",\"r\":\"room:0192f0c1-0000-7000-8000-0000000000aa\"}", FrameError.Malformed)]
    [InlineData("{\"t\":\"sub\",\"r\":\"" + Room + "\",\"e\":\"123\"}", FrameError.Malformed)]
    [InlineData("{\"t\":\"sub\",\"r\":\"" + Room + "\",\"e\":12}", FrameError.Malformed)]
    [InlineData("{\"t\":\"hello\",\"v\":1,\"cid\":\"0192f0c1000070008000000000000001\",\"subs\":[]}", FrameError.Malformed)]
    [InlineData("{\"t\":\"hello\",\"v\":1,\"cid\":\"{0192f0c1-0000-7000-8000-000000000001}\",\"subs\":[]}", FrameError.Malformed)]
    [InlineData("{\"t\":\"hello\",\"v\":1,\"cid\":\"0192f0c1-0000-7000-8000-000000000001\",\"subs\":null}", FrameError.Malformed)]
    [InlineData("{\"t\":\"hello\",\"v\":1,\"cid\":\"0192f0c1-0000-7000-8000-000000000001\",\"subs\":[{\"r\":\"" + Room + "\",\"x\":1}]}", FrameError.Malformed)]
    [InlineData("{\"t\":\"hello\",\"v\":1,\"cid\":\"0192f0c1-0000-7000-8000-000000000001\"}", FrameError.Malformed)]
    public void Malformed_client_frames_are_rejected(string json, FrameError expected) {
        var result = WireParser.ParseClient(Encoding.UTF8.GetBytes(json));

        Assert.Equal(expected, result.Error);
        Assert.Null(result.Frame);
        Assert.NotNull(result.Message);
    }

    [Theory]
    [InlineData("{\"t\":\"cmd\",\"r\":\"" + Room + "\",\"q\":5,\"k\":\"poker.vote.nope\",\"d\":{}}", FrameError.UnknownKind)]
    [InlineData("{\"t\":\"cmd\",\"r\":\"" + Room + "\",\"q\":5,\"k\":\"poker.vote.state\",\"d\":{}}", FrameError.UnknownKind)]
    [InlineData("{\"t\":\"cmd\",\"r\":\"" + Room + "\",\"q\":5,\"k\":\"retro.card.delete\",\"d\":{\"cardId\":\"" + Round + "\"}}", FrameError.WrongRoom)]
    [InlineData("{\"t\":\"cmd\",\"r\":\"" + Room + "\",\"q\":5,\"k\":\"poker.round.reveal\",\"d\":{}}", FrameError.InvalidData)]
    [InlineData("{\"t\":\"cmd\",\"r\":\"" + Room + "\",\"q\":5,\"k\":\"poker.round.reveal\",\"d\":null}", FrameError.InvalidData)]
    [InlineData("{\"t\":\"cmd\",\"r\":\"" + Room + "\",\"q\":5,\"k\":\"poker.round.reveal\",\"d\":[]}", FrameError.InvalidData)]
    [InlineData("{\"t\":\"cmd\",\"r\":\"" + Room + "\",\"q\":5,\"k\":\"poker.round.reveal\",\"d\":{\"roundId\":\"" + Round + "\",\"extra\":1}}", FrameError.InvalidData)]
    [InlineData("{\"t\":\"cmd\",\"r\":\"" + Room + "\",\"q\":5,\"k\":\"poker.round.reveal\",\"d\":{\"RoundId\":\"" + Round + "\"}}", FrameError.InvalidData)]
    [InlineData("{\"t\":\"cmd\",\"r\":\"" + Room + "\",\"q\":5,\"k\":\"poker.round.reveal\",\"d\":{\"roundId\":null}}", FrameError.InvalidData)]
    [InlineData("{\"t\":\"cmd\",\"r\":\"" + Room + "\",\"q\":5,\"k\":\"poker.vote.set\",\"d\":{\"roundId\":\"" + Round + "\",\"points\":\"5\"}}", FrameError.InvalidData)]
    [InlineData("{\"t\":\"cmd\",\"r\":\"" + Room + "\",\"q\":5,\"k\":\"poker.vote.set\",\"d\":{\"roundId\":\"" + Round + "\",\"points\":70000}}", FrameError.InvalidData)]
    [InlineData("{\"t\":\"cmd\",\"r\":\"" + Room + "\",\"q\":5,\"k\":\"poker.round.setTicket\",\"d\":{\"roundId\":\"" + Round + "\",\"baseVersion\":1}}", FrameError.InvalidData)]
    [InlineData("{\"t\":\"cmd\",\"r\":\"" + Room + "\",\"q\":5,\"k\":\"poker.settings.update\",\"d\":{\"presenceEnabled\":true}}", FrameError.InvalidData)]
    [InlineData("{\"t\":\"cmd\",\"r\":\"" + Room + "\",\"q\":5,\"k\":\"poker.round.reorder\",\"d\":{\"order\":[null]}}", FrameError.InvalidData)]
    public void Rejected_commands_keep_room_and_q_so_they_can_be_nacked(string json, FrameError expected) {
        var result = WireParser.ParseClient(Encoding.UTF8.GetBytes(json));

        Assert.Equal(expected, result.Error);
        Assert.Equal(RoomKey.TryParse(Room, out var room) ? room : default, result.Room);
        Assert.Equal(5, result.Q);
    }

    [Fact]
    public void Error_messages_never_echo_payload_values() {
        const string secret = "TOP-SECRET-VALUE";
        var json = "{\"t\":\"cmd\",\"r\":\"" + Room.Replace("poker", "board", StringComparison.Ordinal) + "\",\"q\":5,\"k\":\"retro.card.add\",\"d\":{\"body\":\"" + secret + "\",\"x\":1}}";

        var result = WireParser.ParseClient(Encoding.UTF8.GetBytes(json));

        Assert.Equal(FrameError.InvalidData, result.Error);
        Assert.DoesNotContain(secret, result.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_set_ticket_with_an_explicit_null_clears_the_ticket() {
        var json = "{\"t\":\"cmd\",\"r\":\"" + Room + "\",\"q\":5,\"k\":\"poker.round.setTicket\",\"d\":{\"roundId\":\"" + Round + "\",\"ticketKey\":null,\"baseVersion\":1}}";

        var frame = Assert.IsType<CmdFrame>(WireParser.ParseClient(Encoding.UTF8.GetBytes(json)).Frame);

        Assert.Null(Assert.IsType<PokerRoundSetTicket>(frame.D).TicketKey);
    }

    [Fact]
    public void Frames_up_to_64_KiB_parse_and_larger_ones_are_refused() {
        var prefix = "{\"t\":\"cmd\",\"r\":\"" + Room.Replace("poker", "board", StringComparison.Ordinal)
            + "\",\"q\":5,\"k\":\"retro.card.edit\",\"d\":{\"cardId\":\"" + Round + "\",\"baseVersion\":1,\"body\":\"";
        const string suffix = "\"}}";
        var fill = WireParser.MaxFrameBytes - prefix.Length - suffix.Length;
        var exact = Encoding.UTF8.GetBytes(prefix + new string('x', fill) + suffix);
        var over = Encoding.UTF8.GetBytes(prefix + new string('x', fill + 1) + suffix);

        Assert.Equal(WireParser.MaxFrameBytes, exact.Length);
        Assert.True(WireParser.ParseClient(exact).Ok);
        Assert.Equal(FrameError.TooLarge, WireParser.ParseClient(over).Error);
    }

    [Fact]
    public void Deeply_nested_frames_are_refused() {
        var json = "{\"t\":\"ping\",\"c\":" + new string('[', 100) + new string(']', 100) + "}";

        var result = WireParser.ParseClient(Encoding.UTF8.GetBytes(json));

        Assert.Equal(FrameError.Malformed, result.Error);
        Assert.Equal("not valid JSON", result.Message);
    }

    [Theory]
    [InlineData("{\"t\":\"tx\",\"r\":\"" + Room + "\",\"s\":1,\"ev\":[{\"k\":\"poker.nope\",\"d\":{}}]}", FrameError.UnknownKind)]
    [InlineData("{\"t\":\"tx\",\"r\":\"" + Room + "\",\"s\":1,\"ev\":[{\"k\":\"retro.focus\",\"d\":{}}]}", FrameError.WrongRoom)]
    [InlineData("{\"t\":\"tx\",\"r\":\"" + Room + "\",\"s\":1,\"ev\":[{\"k\":\"poker.round.deleted\",\"d\":{}}]}", FrameError.InvalidData)]
    [InlineData("{\"t\":\"tx\",\"r\":\"" + Room + "\",\"s\":1,\"ev\":[{\"k\":\"poker.round.deleted\"}]}", FrameError.Malformed)]
    [InlineData("{\"t\":\"tx\",\"r\":\"" + Room + "\",\"s\":1,\"ev\":[{\"k\":\"poker.spotlight\",\"d\":{},\"x\":1}]}", FrameError.Malformed)]
    [InlineData("{\"t\":\"tx\",\"r\":\"" + Room + "\",\"s\":1,\"ev\":[{\"k\":\"poker.spotlight\",\"k\":\"poker.spotlight\",\"d\":{}}]}", FrameError.Malformed)]
    [InlineData("{\"t\":\"tx\",\"r\":\"" + Room + "\",\"s\":1,\"ev\":{}}", FrameError.Malformed)]
    [InlineData("{\"t\":\"tx\",\"r\":\"" + Room + "\",\"s\":1,\"ev\":[1]}", FrameError.Malformed)]
    [InlineData("{\"t\":\"replay\",\"r\":\"" + Room + "\",\"e\":\"0123456789abcdef\",\"from\":1,\"lastQ\":0,\"txs\":[{\"s\":1}]}", FrameError.Malformed)]
    [InlineData("{\"t\":\"replay\",\"r\":\"" + Room + "\",\"e\":\"0123456789abcdef\",\"from\":1,\"lastQ\":0,\"txs\":[{\"s\":\"1\",\"ev\":[]}]}", FrameError.Malformed)]
    [InlineData("{\"t\":\"x\",\"r\":\"" + Room + "\",\"k\":\"jira.nope\",\"d\":{}}", FrameError.UnknownKind)]
    [InlineData("{\"t\":\"x\",\"r\":\"team:0192f0c1-0000-7000-8000-0000000000aa\",\"k\":\"jira.meta\",\"d\":{\"key\":\"A-1\"}}", FrameError.WrongRoom)]
    [InlineData("{\"t\":\"nack\",\"r\":\"" + Room + "\",\"q\":1,\"code\":\"server_busy\",\"retry\":false}", FrameError.Malformed)]
    [InlineData("{\"t\":\"nack\",\"r\":\"" + Room + "\",\"q\":1,\"code\":\"no_such_code\",\"retry\":false}", FrameError.Malformed)]
    [InlineData("{\"t\":\"snap\",\"r\":\"team:0192f0c1-0000-7000-8000-0000000000aa\",\"e\":\"0123456789abcdef\",\"s\":1,\"lastQ\":0,\"d\":{},\"o\":{}}", FrameError.Malformed)]
    [InlineData("{\"t\":\"snap\",\"r\":\"" + Room + "\",\"e\":\"0123456789abcdef\",\"s\":1,\"lastQ\":0,\"d\":{}}", FrameError.InvalidData)]
    [InlineData("{\"t\":\"cmd\",\"r\":\"" + Room + "\",\"q\":1,\"k\":\"poker.round.reveal\",\"d\":{}}", FrameError.UnknownType)]
    public void Malformed_server_frames_are_rejected(string json, FrameError expected) {
        var result = WireParser.ParseServer(Encoding.UTF8.GetBytes(json));

        Assert.Equal(expected, result.Error);
        Assert.Null(result.Frame);
    }

    [Fact]
    public void Writing_a_payload_missing_from_the_catalog_fails_loudly() {
        var room = new RoomKey(RoomKind.Poker, Guid.CreateVersion7());

        Assert.Throws<ArgumentException>(() => WireWriter.ToUtf8(new CmdFrame(room, 1, new NotInCatalog())));
    }

    private sealed record NotInCatalog : PokerCommand;
}
