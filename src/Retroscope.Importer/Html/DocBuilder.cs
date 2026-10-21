using System.Collections.Immutable;
using System.Text;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;
using Retroscope.Core.Markup;
using Retroscope.Importer.Archive;
using Retroscope.Importer.Source;

namespace Retroscope.Importer.Html;

/// <summary>
/// Walks an HTML fragment into a <see cref="Doc"/>. Inline formatting is collected as marks on leaves (the way the
/// editor that wrote the HTML models it) and nested only when a paragraph is closed, so markup such as
/// <c>&lt;b&gt;&lt;p&gt;x&lt;/p&gt;&lt;/b&gt;</c> keeps its bold.
/// </summary>
internal sealed partial class DocBuilder(HtmlContext context) {
    private static readonly HtmlParser Parser = new();
    private static readonly IElement FragmentContext = Parser.ParseDocument(string.Empty).Body!;

    private readonly Dictionary<Guid, ExtractedImage> _images = [];
    private int _imageCount;

    public Dictionary<HtmlLoss, int> Losses { get; } = [];

    public IEnumerable<ExtractedImage> Images => _images.Values;

    public Doc Build(string html) {
        // Bodies saved before the rich-text editor are plain text, where a newline is a line break.
        if (!html.Contains('<', StringComparison.Ordinal)) {
            html = html.ReplaceLineEndings("<br>");
        }
        var nodes = Parser.ParseFragment(html, FragmentContext);
        return new Doc(Blocks(nodes, []));
    }

    private List<DocBlock> Blocks(IEnumerable<INode> nodes, ImmutableArray<Mark> marks) {
        var flow = new Flow();
        foreach (var node in nodes) {
            Visit(node, marks, flow);
        }
        flow.Flush();
        return flow.Blocks;
    }

    private void Visit(INode node, ImmutableArray<Mark> marks, Flow flow) {
        if (node is IText text) {
            AddText(Collapse(Clean(text.Data)), marks, flow);
            return;
        }
        if (node is not IElement element) {
            return;
        }

        var tag = element.LocalName;
        if (HtmlTagPolicy.Dropped.Contains(tag)) {
            return;
        }
        switch (tag) {
            case "strong" or "b":
                Children(element, With(marks, Mark.Bold), flow);
                break;
            case "em" or "i":
                Children(element, With(marks, Mark.Italic), flow);
                break;
            case "s" or "strike":
                Children(element, With(marks, Mark.Strike), flow);
                break;
            case "code":
                var code = Collapse(Clean(element.TextContent));
                if (code.Trim().Length > 0) {
                    flow.Add(new DocCode(code), marks);
                }
                break;
            case "a":
                var href = element.GetAttribute("href")?.Trim();
                Children(element, IsLinkable(href) && !marks.Any(x => x.Href is not null) ? With(marks, Mark.Link(href!)) : marks, flow);
                break;
            case "br":
                flow.Add(new DocLineBreak(), WithoutLinks(marks));
                break;
            case "img":
                AddImage(element, marks, flow);
                break;
            case "span" when element.GetAttribute("data-mention-id") is { } mentionId:
                AddMention(mentionId, element.GetAttribute("data-name") ?? element.TextContent, marks, flow);
                break;
            case "p" or "div" or "li":
                flow.Flush();
                Children(element, marks, flow);
                flow.Flush();
                break;
            case "pre":
                flow.Flush();
                flow.Blocks.Add(new DocCodeBlock(CodeLanguage(element), PreText(element)));
                break;
            case "ul" or "ol":
                flow.Flush();
                AddList(element, marks, flow);
                break;
            case "blockquote":
                flow.Flush();
                var quoted = Blocks(element.ChildNodes, marks);
                if (quoted.Count > 0) {
                    flow.Blocks.Add(new DocQuote(quoted));
                }
                break;
            case "h1" or "h2" or "h3" or "h4" or "h5" or "h6" or "hr" when HtmlTagPolicy.TextOnly.Contains(tag):
                // Text only, but a heading or rule still separates paragraphs.
                flow.Flush();
                Children(element, marks, flow);
                flow.Flush();
                break;
            case var _ when HtmlTagPolicy.TextOnly.Contains(tag):
                Children(element, marks, flow);
                break;
            default:
                throw new InvalidOperationException($"<{tag}> is not in the HTML tag policy; the histogram should have refused the run.");
        }
    }

    private void Children(IElement element, ImmutableArray<Mark> marks, Flow flow) {
        foreach (var child in element.ChildNodes) {
            Visit(child, marks, flow);
        }
    }

    private void AddList(IElement list, ImmutableArray<Mark> marks, Flow flow) {
        var items = new List<DocListItem>();
        var loose = new List<INode>();
        void AddItem(IEnumerable<INode> nodes) {
            var blocks = Blocks(nodes, marks);
            if (blocks.Count > 0) {
                items.Add(new DocListItem(blocks));
            }
        }

        foreach (var child in list.ChildNodes) {
            if (child is IElement { LocalName: "li" } item) {
                AddItem(loose);
                loose.Clear();
                AddItem(item.ChildNodes);
            } else {
                loose.Add(child);
            }
        }
        AddItem(loose);

        if (items.Count == 0) {
            return;
        }
        if (list.LocalName == "ul") {
            flow.Blocks.Add(new DocBulletList(items));
        } else {
            var start = int.TryParse(list.GetAttribute("start"), out var value) && value is >= 0 and <= 999_999_999 ? value : 1;
            flow.Blocks.Add(new DocOrderedList(start, items));
        }
    }

    // Text may carry the old editor's own tokens: [[mention:<id>:<name>]] and markdown links, both shown as such.
    private void AddText(string text, ImmutableArray<Mark> marks, Flow flow) {
        var position = 0;
        foreach (Match match in TextToken().Matches(text)) {
            flow.Add(text[position..match.Index], marks);
            if (match.Groups["name"].Success) {
                AddMention(match.Groups["id"].Value, match.Groups["name"].Value, marks, flow);
            } else if (marks.Any(x => x.Href is not null)) {
                flow.Add(match.Value, marks);
            } else {
                flow.Add(match.Groups["label"].Value, With(marks, Mark.Link(match.Groups["href"].Value)));
            }
            position = match.Index + match.Length;
        }
        flow.Add(text[position..], marks);
    }

    private void AddMention(string sourceId, string name, ImmutableArray<Mark> marks, Flow flow) {
        name = Collapse(Clean(name)).Trim().TrimStart('@');
        if (Guid.TryParse(sourceId, out var id) && context.ResolveUser(id) is { } user) {
            flow.Add(new DocMention(user, name), WithoutLinks(marks));
            return;
        }
        HtmlToMarkup.Add(Losses, HtmlLoss.MentionOfNonUser);
        flow.Add("@" + name, marks);
    }

    private void AddImage(IElement element, ImmutableArray<Mark> marks, Flow flow) {
        var source = element.GetAttribute("src")?.Trim() ?? "";
        if (!source.StartsWith("data:image/", StringComparison.OrdinalIgnoreCase)) {
            if (IsLinkable(source) && source.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !marks.Any(x => x.Href is not null)) {
                flow.Add("image", With(marks, Mark.Link(source)));
            } else {
                HtmlToMarkup.Add(Losses, HtmlLoss.ImageUnlinkable);
                flow.Add("[image]", marks);
            }
            return;
        }

        var loss = Extract(source, out var image);
        if (loss is not null) {
            HtmlToMarkup.Add(Losses, loss.Value);
            flow.Add(HtmlToMarkup.RemovedImage, marks);
            return;
        }
        _images.TryAdd(image!.BlobId, image);
        _imageCount++;
        flow.Add(new DocImage(image.BlobId, "image"), WithoutLinks(marks));
    }

    private HtmlLoss? Extract(string dataUrl, out ExtractedImage? image) {
        image = null;
        var comma = dataUrl.IndexOf(',', StringComparison.Ordinal);
        if (comma < 0 || !dataUrl[..comma].EndsWith(";base64", StringComparison.OrdinalIgnoreCase)) {
            return HtmlLoss.ImageNotRecognised;
        }
        var payload = Base64Whitespace().Replace(dataUrl[(comma + 1)..], "");
        // Refuse payloads far over the cap before decoding them; padding makes the estimate at most 2 bytes high.
        if ((long)payload.Length * 3 / 4 > HtmlToMarkup.MaxImageBytes + 2) {
            return HtmlLoss.ImageOversize;
        }
        byte[] bytes;
        try {
            bytes = Convert.FromBase64String(payload);
        } catch (FormatException) {
            return HtmlLoss.ImageNotRecognised;
        }
        if (bytes.Length > HtmlToMarkup.MaxImageBytes) {
            return HtmlLoss.ImageOversize;
        }
        if (bytes.Length == 0 || ImageSniffer.Detect(bytes) is not { } contentType) {
            return HtmlLoss.ImageNotRecognised;
        }
        if (context.BoardId is not { } boardId) {
            return HtmlLoss.ImageWithoutBoard;
        }
        if (_imageCount >= MarkupLimits.ImagesPerBody) {
            return HtmlLoss.ImageOverLimit;
        }
        image = HtmlToMarkup.Image(boardId, contentType, bytes);
        return null;
    }

    private static string? CodeLanguage(IElement pre) {
        var code = pre.Children.FirstOrDefault(x => x.LocalName == "code");
        var language = code?.ClassList.FirstOrDefault(x => x.StartsWith("language-", StringComparison.Ordinal));
        return language?["language-".Length..];
    }

    private static string PreText(IElement pre) {
        var text = new StringBuilder();
        void Walk(INode node) {
            switch (node) {
                case IText value:
                    text.Append(Clean(value.Data));
                    break;
                case IElement { LocalName: "br" }:
                    text.Append('\n');
                    break;
                case IElement element when !HtmlTagPolicy.Dropped.Contains(element.LocalName):
                    foreach (var child in element.ChildNodes) {
                        Walk(child);
                    }
                    break;
            }
        }

        foreach (var child in pre.ChildNodes) {
            Walk(child);
        }
        return text.ToString().ReplaceLineEndings("\n").TrimEnd('\n');
    }

    private static bool IsLinkable(string? href) =>
        href is { Length: > 0 and <= 2048 }
        && (href.StartsWith("http://", StringComparison.OrdinalIgnoreCase) && href.Length > 7
            || href.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && href.Length > 8
            || href.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) && href.Length > 7)
        && href.All(c => c > ' ' && c != '\u007f');

    private static ImmutableArray<Mark> With(ImmutableArray<Mark> marks, Mark mark) =>
        marks.Any(x => x.Kind == mark.Kind) ? marks : marks.Add(mark);

    private static ImmutableArray<Mark> WithoutLinks(ImmutableArray<Mark> marks) => marks.RemoveAll(x => x.Href is not null);

    // Postgres text cannot hold NUL, and no stored body may contain other C0 controls or unpaired surrogates.
    private static string Clean(string text) {
        StringBuilder? output = null;
        for (var i = 0; i < text.Length; i++) {
            var c = text[i];
            var pair = char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]);
            var bad = !pair && char.IsSurrogate(c) || c < ' ' && c is not ('\t' or '\n' or '\r');
            if (bad) {
                output ??= new StringBuilder(text, 0, i, text.Length);
            } else {
                output?.Append(c);
                if (pair) {
                    output?.Append(text[i + 1]);
                    i++;
                }
            }
        }
        return output?.ToString() ?? text;
    }

    private static string Collapse(string text) => HtmlWhitespace().Replace(text, " ");

    [GeneratedRegex(@"[ \t\n\r\f]+", RegexOptions.CultureInvariant)]
    private static partial Regex HtmlWhitespace();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Base64Whitespace();

    [GeneratedRegex(@"\[\[mention:(?<id>[^:\]]+):(?<name>[^\]]+)\]\]|\[(?<label>[^\]]+)\]\((?<href>https?://[^)\s]+)\)", RegexOptions.CultureInvariant)]
    private static partial Regex TextToken();

    private enum MarkKind {
        Bold,
        Italic,
        Strike,
        Link,
    }

    private readonly record struct Mark(MarkKind Kind, string? Href) {
        public static Mark Bold => new(MarkKind.Bold, null);

        public static Mark Italic => new(MarkKind.Italic, null);

        public static Mark Strike => new(MarkKind.Strike, null);

        public static Mark Link(string href) => new(MarkKind.Link, href);
    }

    private readonly record struct Leaf(DocInline Node, ImmutableArray<Mark> Marks);

    /// <summary>The blocks of one container plus the paragraph being collected.</summary>
    private sealed class Flow {
        private readonly List<Leaf> _leaves = [];

        public List<DocBlock> Blocks { get; } = [];

        public void Add(string text, ImmutableArray<Mark> marks) {
            if (text.Length > 0) {
                _leaves.Add(new Leaf(new DocText(text), marks));
            }
        }

        public void Add(DocInline node, ImmutableArray<Mark> marks) => _leaves.Add(new Leaf(node, marks));

        public void Flush() {
            var content = Nest(Trim(_leaves));
            _leaves.Clear();
            if (content.Count > 0) {
                Blocks.Add(new DocParagraph(content));
            }
        }

        // HTML whitespace rules: one space between words, none at a line start or end.
        private static List<Leaf> Trim(List<Leaf> leaves) {
            var result = new List<Leaf>();
            var atLineStart = true;
            foreach (var leaf in leaves) {
                if (leaf.Node is DocText text) {
                    var value = atLineStart || EndsWithSpace(result) ? text.Value.TrimStart(' ') : text.Value;
                    if (value.Length > 0) {
                        result.Add(leaf with { Node = new DocText(value) });
                        atLineStart = false;
                    }
                    continue;
                }
                if (leaf.Node is DocLineBreak) {
                    TrimTrailingSpace(result);
                    atLineStart = true;
                } else {
                    atLineStart = false;
                }
                result.Add(leaf);
            }
            TrimTrailingSpace(result);
            while (result.Count > 0 && result[^1].Node is DocLineBreak) {
                result.RemoveAt(result.Count - 1);
                TrimTrailingSpace(result);
            }
            while (result.Count > 0 && result[0].Node is DocLineBreak) {
                result.RemoveAt(0);
            }
            return result;
        }

        private static bool EndsWithSpace(List<Leaf> leaves) => leaves.Count > 0 && leaves[^1].Node is DocText { Value: [.., ' '] };

        private static void TrimTrailingSpace(List<Leaf> leaves) {
            while (leaves.Count > 0 && leaves[^1].Node is DocText text) {
                var value = text.Value.TrimEnd(' ');
                if (value.Length > 0) {
                    leaves[^1] = leaves[^1] with { Node = new DocText(value) };
                    return;
                }
                leaves.RemoveAt(leaves.Count - 1);
            }
        }

        private static List<DocInline> Nest(List<Leaf> leaves) {
            var stack = new List<(Mark? Mark, List<DocInline> Content)> { (null, []) };
            foreach (var leaf in leaves) {
                var shared = 0;
                while (shared < stack.Count - 1 && shared < leaf.Marks.Length && stack[shared + 1].Mark == leaf.Marks[shared]) {
                    shared++;
                }
                while (stack.Count - 1 > shared) {
                    Close(stack);
                }
                foreach (var mark in leaf.Marks.Skip(shared)) {
                    stack.Add((mark, []));
                }
                stack[^1].Content.Add(leaf.Node);
            }
            while (stack.Count > 1) {
                Close(stack);
            }
            return stack[0].Content;
        }

        // Emphasis cannot open before or close after whitespace, so edge spaces and breaks move outside it.
        private static void Close(List<(Mark? Mark, List<DocInline> Content)> stack) {
            var (mark, content) = stack[^1];
            stack.RemoveAt(stack.Count - 1);
            var parent = stack[^1].Content;
            if (mark!.Value.Kind == MarkKind.Link) {
                if (content.Count > 0) {
                    parent.Add(new DocLink(mark.Value.Href!, content));
                }
                return;
            }

            var leading = new List<DocInline>();
            var trailing = new List<DocInline>();
            while (content.Count > 0 && SplitEdge(content, 0, leading, atStart: true)) {
            }
            while (content.Count > 0 && SplitEdge(content, content.Count - 1, trailing, atStart: false)) {
            }
            trailing.Reverse();
            parent.AddRange(leading);
            if (content.Count > 0) {
                parent.Add(new DocStyled(mark.Value.Kind switch {
                    MarkKind.Bold => DocStyle.Bold,
                    MarkKind.Italic => DocStyle.Italic,
                    _ => DocStyle.Strike,
                }, content));
            }
            parent.AddRange(trailing);
        }

        // Moves whitespace or a line break at one edge of content into moved; true when the edge is now clean only
        // after removing a whole node (so the caller looks again).
        private static bool SplitEdge(List<DocInline> content, int index, List<DocInline> moved, bool atStart) {
            switch (content[index]) {
                case DocLineBreak node:
                    content.RemoveAt(index);
                    moved.Add(node);
                    return true;
                case DocText text:
                    var trimmed = atStart ? text.Value.TrimStart() : text.Value.TrimEnd();
                    if (trimmed.Length == text.Value.Length) {
                        return false;
                    }
                    moved.Add(new DocText(atStart ? text.Value[..^trimmed.Length] : text.Value[trimmed.Length..]));
                    if (trimmed.Length == 0) {
                        content.RemoveAt(index);
                        return true;
                    }
                    content[index] = new DocText(trimmed);
                    return false;
                default:
                    return false;
            }
        }
    }
}
