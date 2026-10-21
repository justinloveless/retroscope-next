using System.Text.Json;
using System.Text.Json.Serialization;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Endpoints;

namespace Retroscope.Server.Json;

[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(QuickSessionRequest))]
[JsonSerializable(typeof(QuickBoardRequest))]
[JsonSerializable(typeof(TeamBoardRequest))]
[JsonSerializable(typeof(BoardAccessRequest))]
[JsonSerializable(typeof(UnlockRequest))]
[JsonSerializable(typeof(DeleteBoardRequest))]
[JsonSerializable(typeof(ActionItemCommentRequest))]
[JsonSerializable(typeof(RoomCreated))]
[JsonSerializable(typeof(RoomLookup))]
[JsonSerializable(typeof(ListPage<BoardListItem>))]
[JsonSerializable(typeof(ListPage<PokerSessionListItem>))]
[JsonSerializable(typeof(ListPage<ActionItemListItem>))]
[JsonSerializable(typeof(MentionsPage))]
[JsonSerializable(typeof(ActionItemComments))]
[JsonSerializable(typeof(ActionItemCommentPosted))]
public sealed partial class RoomHttpJsonContext : JsonSerializerContext {
}
