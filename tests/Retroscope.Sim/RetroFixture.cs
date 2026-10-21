using System.Text.Json;
using Retroscope.Core.Protocol;

namespace Retroscope.Core.Tests.Retro;

/// <summary>The one member of the Core tests' board fixture that the linked <see cref="RetroViewerModel"/> uses.</summary>
internal static class RetroFixture {
    public static string Json(object payload) => JsonSerializer.Serialize(payload, WireJsonContext.Default.GetTypeInfo(payload.GetType())!);
}
