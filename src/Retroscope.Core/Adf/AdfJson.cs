using System.Text.Json;

namespace Retroscope.Core.Adf;

// Defensive reads over Jira's ADF: a missing or mistyped member reads as absent, never as an exception.
internal static class AdfJson {
    public static string? Type(JsonElement node) {
        return node.ValueKind == JsonValueKind.Object && node.TryGetProperty("type", out var type) ? String(type) : null;
    }

    public static IEnumerable<JsonElement> Children(JsonElement node, string member = "content") {
        if (node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty(member, out var content)
            && content.ValueKind == JsonValueKind.Array) {
            return content.EnumerateArray();
        }
        return [];
    }

    public static string? Attr(JsonElement node, string name) {
        return TryAttr(node, name, out var value) ? String(value) : null;
    }

    public static bool TryAttr(JsonElement node, string name, out JsonElement value) {
        value = default;
        return node.ValueKind == JsonValueKind.Object
            && node.TryGetProperty("attrs", out var attrs)
            && attrs.ValueKind == JsonValueKind.Object
            && attrs.TryGetProperty(name, out value);
    }

    public static double? Number(JsonElement node, string attr) {
        return TryAttr(node, attr, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;
    }

    public static string? Text(JsonElement node) {
        return node.ValueKind == JsonValueKind.Object && node.TryGetProperty("text", out var text) ? String(text) : null;
    }

    // An escaped lone surrogate only fails when the string is transcoded; it reads as U+FFFD instead.
    public static string? String(JsonElement value) {
        if (value.ValueKind != JsonValueKind.String) {
            return null;
        }
        try {
            return value.GetString();
        } catch (InvalidOperationException) {
            return "\uFFFD";
        }
    }
}
