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
