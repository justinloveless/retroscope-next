using System.Text.Json;

namespace Retroscope.Core.Adf;

// One entry of the issue's `attachment` field. Id is the REST attachment id the proxy serves (§f).
public sealed record AdfAttachment(string Id, string FileName, string MimeType, DateTimeOffset Created) {
    private static readonly HashSet<string> ImageTypes = new(StringComparer.OrdinalIgnoreCase) {
        "image/png",
        "image/jpeg",
        "image/gif",
        "image/webp",
    };

    // The content types the attachment proxy serves; anything else is offered as a link to Jira.
    public bool IsImage => ImageTypes.Contains(MimeType);
}

// ADF media nodes carry Media Services file ids, not REST attachment ids (F20). A media node is matched by its
// filename (attrs.alt); when several attachments share that name, the n-th distinct media id with the name takes the
// n-th upload.
internal sealed class AdfMediaMap {
    private readonly Dictionary<string, AdfAttachment> _byMediaId;

    private AdfMediaMap(Dictionary<string, AdfAttachment> byMediaId) {
        _byMediaId = byMediaId;
    }

    public static AdfMediaMap Build(JsonElement root, IReadOnlyList<AdfAttachment> attachments) {
        var uploads = attachments
            .GroupBy(attachment => attachment.FileName, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(attachment => attachment.Created).ThenBy(attachment => attachment.Id, UploadIdComparer.Instance).ToList(),
                StringComparer.Ordinal);
        var taken = new Dictionary<string, int>(StringComparer.Ordinal);
        var byMediaId = new Dictionary<string, AdfAttachment>(StringComparer.Ordinal);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var media in MediaInDocumentOrder(root)) {
            var id = AdfJson.Attr(media, "id");
            var name = AdfJson.Attr(media, "alt");
            if (id == null || !seen.Add(id) || name == null || !uploads.TryGetValue(name, out var candidates)) {
                continue;
            }
            var index = taken.GetValueOrDefault(name);
            taken[name] = index + 1;
            if (index < candidates.Count) {
                byMediaId[id] = candidates[index];
            }
        }
        return new AdfMediaMap(byMediaId);
    }

    public AdfAttachment? Resolve(string? mediaId) {
        return mediaId != null && _byMediaId.TryGetValue(mediaId, out var attachment) ? attachment : null;
    }

    private static IEnumerable<JsonElement> MediaInDocumentOrder(JsonElement root) {
        var stack = new Stack<JsonElement>();
        stack.Push(root);
        while (stack.Count > 0) {
            var node = stack.Pop();
            var type = AdfJson.Type(node);
            if (type is "media" or "mediaInline" && AdfJson.Attr(node, "type") is "file" or "link") {
                yield return node;
            }
            foreach (var child in AdfJson.Children(node).Reverse()) {
                stack.Push(child);
            }
        }
    }

    // Jira attachment ids are decimal strings; a shorter one is older.
    private sealed class UploadIdComparer : IComparer<string> {
        public static readonly UploadIdComparer Instance = new();

        public int Compare(string? x, string? y) {
            var byLength = (x?.Length ?? 0).CompareTo(y?.Length ?? 0);
            return byLength != 0 ? byLength : string.CompareOrdinal(x, y);
        }
    }
}
