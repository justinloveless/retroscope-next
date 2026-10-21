using System.Text.Json;
using Retroscope.Core.Protocol;
using Retroscope.Core.Tests.Retro;

namespace Retroscope.Sim;

/// <summary>What a tab's board shows, read back from the reference client fold to drive the workload.</summary>
internal static class RetroView {
    public static (RetroSnapshot Snapshot, RetroOverlay Overlay) Of(RetroViewerModel model) {
        var lines = model.ToJson().Split('\n');
        return (
            JsonSerializer.Deserialize(lines[0], WireJsonContext.Default.RetroSnapshot)!,
            JsonSerializer.Deserialize(lines[1], WireJsonContext.Default.RetroOverlay)!);
    }
}
