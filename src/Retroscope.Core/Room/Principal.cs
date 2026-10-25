using System.Text.Json.Serialization;
using Retroscope.Core.Ids;

namespace Retroscope.Core.Room;

[JsonConverter(typeof(StrictEnumConverter<PrincipalKind>))]
public enum PrincipalKind : byte {
    [JsonStringEnumMemberName("user")] User,
    [JsonStringEnumMemberName("guest")] Guest,
    [JsonStringEnumMemberName("external")] External,
}

[JsonConverter(typeof(StrictEnumConverter<TeamRole>))]
public enum TeamRole : byte {
    [JsonStringEnumMemberName("member")] Member,
    [JsonStringEnumMemberName("admin")] Admin,
    [JsonStringEnumMemberName("owner")] Owner,
}

public sealed record Principal(PrincipalId Id, PrincipalKind Kind, string DisplayName, BlobId? AvatarBlobId, bool IsAppAdmin);

/// <summary>
/// Identity only. The role is resolved at decide time through <see cref="IAccessView"/> (R12), never cached at
/// subscribe, so a removal or a demotion applies to the very next command.
/// </summary>
public sealed record Viewer(Principal Who, ClientId Client, ConnectionId Conn);
