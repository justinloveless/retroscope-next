namespace Retroscope.Core.Adf;

// ADF stores colours as #rrggbb picked from the Jira editor palette (@atlaskit/editor-palette 3.5.80). Jira shows
// each palette colour as a design token that changes with the theme, so a palette hex becomes its token name
// ("blue-bolder", "text"…) and the client's stylesheet holds the light and dark values. Any other valid hex passes
// through, lower-cased, and shows as written in both themes, as it does in Jira. Anything else is dropped.
internal static class AdfColor {
    private static readonly Dictionary<string, string> TextPalette = new(StringComparer.OrdinalIgnoreCase) {
        ["#B3D4FF"] = "blue-subtler",
        ["#4C9AFF"] = "blue-bold",
        ["#0747A6"] = "blue-bolder",
        ["#B3F5FF"] = "teal-subtler",
        ["#00B8D9"] = "teal-bold",
        ["#008DA6"] = "teal-bolder",
        ["#ABF5D1"] = "green-subtler",
        ["#36B37E"] = "green-bold",
        ["#006644"] = "green-bolder",
        ["#FFF0B3"] = "yellow-subtler",
        ["#FFC400"] = "orange-subtle",
        ["#FF991F"] = "orange-bold",
        ["#FFBDAD"] = "red-subtler",
        ["#FF5630"] = "red-bold",
        ["#BF2600"] = "red-bolder",
        ["#EAE6FF"] = "purple-subtler",
        ["#6554C0"] = "purple-bold",
        ["#403294"] = "purple-bolder",
        ["#FFFFFF"] = "inverse",
        ["#97A0AF"] = "gray-bold",
        ["#172B4D"] = "text",
        ["#D3F1A7"] = "lime-subtler",
        ["#6A9A23"] = "lime-bold",
        ["#4C6B1F"] = "lime-bolder",
        ["#FCE4A6"] = "orange-subtler",
        ["#E06C00"] = "orange-bold",
        ["#9E4C00"] = "orange-bolder",
        ["#FDD0EC"] = "magenta-subtler",
        ["#CD519D"] = "magenta-bold",
        ["#943D73"] = "magenta-bolder",
        ["#B38600"] = "yellow-bold",
        ["#7F5F01"] = "yellow-bolder",
    };

    // The background palette (highlights, table cells, custom panels) plus the text-highlight palette. The
    // "subtler-hovered" shades map to "subtler", the nearest token the stylesheet carries.
    private static readonly Dictionary<string, string> BackgroundPalette = new(StringComparer.OrdinalIgnoreCase) {
        ["#DEEBFF"] = "blue-subtlest",
        ["#B3D4FF"] = "blue-subtler",
        ["#4C9AFF"] = "blue-subtle",
        ["#ADCBFB"] = "blue-subtler",
        ["#E6FCFF"] = "teal-subtlest",
        ["#B3F5FF"] = "teal-subtler",
        ["#79E2F2"] = "teal-subtle",
        ["#B1E4F7"] = "teal-subtler",
        ["#C6EDFB"] = "teal-subtler",
        ["#E3FCEF"] = "green-subtlest",
        ["#ABF5D1"] = "green-subtler",
        ["#57D9A3"] = "green-subtle",
        ["#97EDC9"] = "green-subtler",
        ["#EFFFD6"] = "lime-subtlest",
        ["#D3F1A7"] = "lime-subtler",
        ["#BDE97C"] = "lime-subtler",
        ["#FFFAE6"] = "yellow-subtlest",
        ["#FFF0B3"] = "yellow-subtler",
        ["#F8E6A0"] = "yellow-subtler",
        ["#FFC400"] = "orange-subtle",
        ["#EFDD4E"] = "yellow-subtler",
        ["#FFF5DB"] = "orange-subtlest",
        ["#FCE4A6"] = "orange-subtler",
        ["#FEDEC8"] = "orange-subtler",
        ["#FBD779"] = "orange-subtler",
        ["#FFEBE6"] = "red-subtlest",
        ["#FFBDAD"] = "red-subtler",
        ["#FFD5D2"] = "red-subtler",
        ["#FF8F73"] = "red-subtle",
        ["#FFB8B2"] = "red-subtler",
        ["#FFECF8"] = "magenta-subtlest",
        ["#FDD0EC"] = "magenta-subtler",
        ["#FCB6E1"] = "magenta-subtler",
        ["#EAE6FF"] = "purple-subtlest",
        ["#C0B6F2"] = "purple-subtler",
        ["#DFD8FD"] = "purple-subtler",
        ["#998DD9"] = "purple-subtle",
        ["#E3BDFA"] = "purple-subtler",
        ["#FFFFFF"] = "surface",
        ["#F4F5F7"] = "gray-subtlest",
        ["#DCDFE4"] = "gray-subtler",
        ["#B3BAC5"] = "gray-subtle",
        ["#B7B9BE"] = "gray-subtler",
    };

    public static string? Text(string? hex) => Resolve(hex, TextPalette);

    public static string? Background(string? hex) => Resolve(hex, BackgroundPalette);

    private static string? Resolve(string? hex, Dictionary<string, string> palette) {
        if (!IsHex(hex)) {
            return null;
        }
        return palette.TryGetValue(hex!, out var token) ? token : hex!.ToLowerInvariant();
    }

    // Strictly #rrggbb: the value reaches the client's CSSOM, so nothing else may pass.
    private static bool IsHex(string? value) {
        if (value is not { Length: 7 } || value[0] != '#') {
            return false;
        }
        for (var i = 1; i < 7; i++) {
            if (!char.IsAsciiHexDigit(value[i])) {
                return false;
            }
        }
        return true;
    }
}
