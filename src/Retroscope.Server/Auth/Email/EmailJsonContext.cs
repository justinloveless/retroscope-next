using System.Text.Json;
using System.Text.Json.Serialization;

namespace Retroscope.Server.Auth.Email;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record EmailRequest(string? Email);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TokenRequest(string? Token);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ResetConsumeRequest(string? Token, string? Password);

/// <param name="Email">Masked: <c>j•••@example.com</c>.</param>
public sealed record MagicPreviewResponse(string Email);

// Registered by EmailModule rather than in Program.cs's chain: no unit owns Server/Json for these routes (U0 decision 3).
[JsonSourceGenerationOptions(
    JsonSerializerDefaults.Web,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(EmailRequest))]
[JsonSerializable(typeof(TokenRequest))]
[JsonSerializable(typeof(ResetConsumeRequest))]
[JsonSerializable(typeof(MagicPreviewResponse))]
public sealed partial class EmailJsonContext : JsonSerializerContext {
}
