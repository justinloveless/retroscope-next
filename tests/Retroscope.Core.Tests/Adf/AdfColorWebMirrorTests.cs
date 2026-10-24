using System.Reflection;
using System.Text.Json;
using Retroscope.Core.Adf;
using Retroscope.Core.Tests.Scaffold;

namespace Retroscope.Core.Tests.Adf;

/// <summary>
/// The editor shows ADF colours through the same design tokens as the read view, so its palette
/// (<c>web/src/rich/palette.json</c>) must map every hex exactly as <see cref="AdfColor"/> does, entry for entry.
/// </summary>
[Trait("Category", "Adf")]
public sealed class AdfColorWebMirrorTests {
    private static readonly JsonElement Web = Load();

    [Theory]
    [InlineData("text", "TextPalette")]
    [InlineData("background", "BackgroundPalette")]
    public void Each_web_palette_is_the_servers(string member, string field) {
        var server = (Dictionary<string, string>)typeof(AdfColor).GetField(field, BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        var web = Web.GetProperty(member).EnumerateObject().ToDictionary(entry => entry.Name, entry => entry.Value.GetString()!, StringComparer.Ordinal);

        Assert.Equal(
            server.Select(entry => $"{entry.Key.ToLowerInvariant()}={entry.Value}").Order(StringComparer.Ordinal),
            web.Select(entry => $"{entry.Key}={entry.Value}").Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("textMenu", "text")]
    [InlineData("highlightMenu", "background")]
    public void Every_menu_colour_has_a_token(string menu, string palette) {
        foreach (var entry in Web.GetProperty(menu).EnumerateArray()) {
            var hex = entry.GetProperty("hex").GetString()!;
            Assert.True(AdfSchema.IsColor(hex) && hex.Equals(hex.ToLowerInvariant(), StringComparison.Ordinal), hex);
            Assert.True(Web.GetProperty(palette).TryGetProperty(hex, out _), $"{menu} {hex} has no token");
        }
    }

    private static JsonElement Load() {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(RepoRoot.Path, "web/src/rich/palette.json")));
        return document.RootElement.Clone();
    }
}
