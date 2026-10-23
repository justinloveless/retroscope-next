using System.Globalization;
using System.Text;

namespace Retroscope.Core.Tests.Adf;

/// <summary>
/// Random ADF descriptions over the schema's grammar, mostly valid, with the things Jira also sends: preserve-only
/// nodes, unknown nodes, marks and attributes, values the editor refuses, ragged tables and repeated task ids.
/// Deterministic per seed.
/// </summary>
internal sealed class AdfGen(int seed) {
    private static readonly string[] Words = [
        "a", "Hello", "café", "🚀 go", "<b>&amp;</b>", "quote \\\" back \\\\ slash", "tab\\there", "x\\ny", "über ñ 中文",
        "\\u00e9scaped", "  spaced  ",
    ];

    private readonly Random _random = new(seed);
    private int _ids;

    public string Doc() {
        var blocks = Many(0, 6, () => Block("doc", 1));
        return $$"""{"type":"doc","version":1,"content":[{{blocks}}]}""";
    }

    private string Block(string context, int depth) {
        if (depth > 6) {
            return Paragraph(context);
        }
        var choices = context switch {
            "doc" => new[] { "p", "h", "ul", "ol", "quote", "code", "panel", "rule", "table", "tasks", "decisions", "expand", "layout", "media", "extension", "mystery", "card", "group" },
            "cell" => ["p", "h", "ul", "ol", "quote", "code", "panel", "rule", "tasks", "decisions", "nested", "media", "extension"],
            "listItem" => ["p", "ul", "ol", "tasks", "code", "media", "extension"],
            "panel" => ["p", "h", "ul", "ol", "code", "rule", "tasks", "decisions", "extension"],
            "quote" => ["p", "ul", "ol", "code", "extension"],
            "expand" => ["p", "h", "ul", "code", "panel", "table", "nested", "extension"],
            _ => ["p", "h", "ul", "code", "extension"],
        };
        var pick = choices[_random.Next(choices.Length)];
        return pick switch {
            "p" => Paragraph(context),
            "h" => $$"""{"type":"heading","attrs":{"level":{{(Chance(10) ? 9 : _random.Next(1, 7))}}{{LocalId()}}{{Future()}}}{{BlockMarks(context, heading: true)}},"content":[{{Inline(0, 3)}}]}""",
            "ul" => $$"""{"type":"bulletList","content":[{{Many(1, 3, () => ListItem(depth))}}]}""",
            "ol" => $$"""{"type":"orderedList","attrs":{"order":{{_random.Next(0, 5)}}},"content":[{{Many(1, 3, () => ListItem(depth))}}]}""",
            "quote" => $$"""{"type":"blockquote","content":[{{Many(1, 2, () => Block("quote", depth + 1))}}]}""",
            "code" => $$"""{"type":"codeBlock","attrs":{"language":"{{Pick("csharp", "js", "Protocol Buffers")}}"{{Future()}}},"content":[{"type":"text","text":"line 1\nline \"2\""}]}""",
            "panel" => $$"""{"type":"panel","attrs":{"panelType":"{{Pick("info", "note", "custom", "warning")}}"{{(Chance(3) ? ",\"panelColor\":\"#DEEBFF\"" : "")}}},"content":[{{Many(1, 2, () => Block("panel", depth + 1))}}]}""",
            "rule" => """{"type":"rule"}""",
            "table" => Table(depth),
            "tasks" => Tasks(2),
            "decisions" => $$"""{"type":"decisionList","attrs":{"localId":"{{Id("dl")}}"},"content":[{"type":"decisionItem","attrs":{"localId":"{{Id("d")}}","state":"DECIDED"},"content":[{{Inline(0, 2)}}]}]}""",
            "expand" => $$"""{"type":"expand","attrs":{"title":"More"},"content":[{{Many(1, 2, () => Block("expand", depth + 1))}}]}""",
            "nested" => $$"""{"type":"nestedExpand","attrs":{"title":"Inner"},"content":[{{Paragraph("nestedExpand")}}]}""",
            "layout" => $$"""{"type":"layoutSection","content":[{{Many(2, 3, () => $$"""{"type":"layoutColumn","attrs":{"width":{{Pick("50", "33.33", "120")}}},"content":[{{Block("block", depth + 1)}}]}""")}}]}""",
            "media" => $$$"""{"type":"mediaSingle","attrs":{"layout":"center"{{{(Chance(2) ? ",\"width\":640,\"widthType\":\"pixel\"" : "")}}}},"content":[{"type":"media","attrs":{"type":"file","id":"{{{Id("m")}}}","collection":"","alt":"shot.png","width":1280,"height":720}}{{{(Chance(2) ? $$$""",{"type":"caption","content":[{{{Inline(0, 2)}}}]}""" : "")}}}]}""",
            "group" => $$$"""{"type":"mediaGroup","content":[{"type":"media","attrs":{"type":"file","id":"{{{Id("g")}}}","collection":""}}]}""",
            "extension" => $$$"""{"type":"extension","attrs":{"extensionKey":"k{{{_random.Next(3)}}}","extensionType":"com.example","parameters":{"n":{{{_random.Next(5)}}}""" + "}}}",
            "mystery" => """{"type":"futureBlock","attrs":{"x":1},"content":[{"type":"paragraph"}]}""",
            _ => $$$"""{"type":"blockCard","attrs":{"url":"{{{Pick("https://example.com/c", "javascript:alert(1)")}}}"}}""",
        };
    }

    private string Paragraph(string context) =>
        $$"""{"type":"paragraph"{{(Chance(4) ? $$""","attrs":{{{LocalId(first: true)}}}""" : "")}}{{BlockMarks(context, heading: false)}},"content":[{{Inline(0, 4)}}]}""";

    private string BlockMarks(string context, bool heading) {
        if (context is not ("doc" or "block" or "cell") || !Chance(4)) {
            return "";
        }
        var mark = Pick(
            """{"type":"alignment","attrs":{"align":"center"}}""",
            """{"type":"indentation","attrs":{"level":2}}""",
            """{"type":"fontSize","attrs":{"fontSize":"small"}}""",
            """{"type":"alignment","attrs":{"align":"center"}},{"type":"indentation","attrs":{"level":1}}""");
        return $$""","marks":[{{mark}}]""";
    }

    private string ListItem(int depth) =>
        $$"""{"type":"listItem","content":[{{Paragraph("listItem")}}{{(Chance(3) ? "," + Block("listItem", depth + 1) : "")}}]}""";

    private string Tasks(int depth) {
        var items = Many(1, 3, () => Chance(5) && depth > 0
            ? Tasks(depth - 1)
            : $$"""{"type":"taskItem","attrs":{"localId":"{{(Chance(6) ? "dup" : Id("t"))}}","state":"{{Pick("TODO", "DONE")}}"},"content":[{{Inline(0, 2)}}]}""");
        return $$"""{"type":"taskList","attrs":{"localId":"{{Id("tl")}}"},"content":[{{items}}]}""";
    }

    private string Table(int depth) {
        var rows = _random.Next(1, 4);
        var columns = _random.Next(1, 4);
        var ragged = Chance(5);
        var localId = Chance(2) ? $$""","attrs":{"localId":"{{Id("tbl")}}"}""" : "";
        var body = new List<string>();
        for (var r = 0; r < rows; r++) {
            var width = ragged && r == rows - 1 ? Math.Max(1, columns - 1) : columns;
            var cells = Enumerable.Range(0, width).Select(c => {
                var type = r == 0 ? "tableHeader" : "tableCell";
                var attrs = Chance(3) ? $$""","attrs":{"background":"{{Pick("#DEEBFF", "var(--ds-x)")}}","colwidth":[{{100 + c}}]}""" : "";
                return $$"""{"type":"{{type}}"{{attrs}},"content":[{{Block("cell", depth + 1)}}]}""";
            });
            body.Add($$"""{"type":"tableRow","content":[{{string.Join(',', cells)}}]}""");
        }
        return $$"""{"type":"table"{{localId}},"content":[{{string.Join(',', body)}}]}""";
    }

    private string Inline(int min, int max) => Many(min, max, InlineNode);

    private string InlineNode() {
        var pick = _random.Next(16);
        return pick switch {
            < 7 => $$"""{"type":"text","text":"{{Pick(Words)}}"{{TextMarks()}}}""",
            7 => """{"type":"hardBreak"}""",
            8 => $$$"""{"type":"mention","attrs":{"id":"acc-{{{_random.Next(3)}}}","text":"@Someone","accessLevel":""}}""",
            9 => """{"type":"emoji","attrs":{"shortName":":rocket:","id":"1f680","text":"🚀"}}""",
            10 => """{"type":"date","attrs":{"timestamp":"1761955200000"}}""",
            11 => $$$"""{"type":"status","attrs":{"text":"OPEN","color":"{{{Pick("blue", "#123456")}}}","style":"{{{Pick("bold", "loud")}}}"}}""",
            12 => $$$"""{"type":"inlineCard","attrs":{"url":"{{{Pick("https://acme.atlassian.net/browse/RS-1", "ftp://x")}}}"}}""",
            13 => """{"type":"inlineExtension","attrs":{"extensionKey":"x","extensionType":"y"}}""",
            14 => """{"type":"mediaInline","attrs":{"id":"mi","collection":""}}""",
            _ => """{"type":"placeholder","attrs":{"text":"Type here"}}""",
        };
    }

    private string TextMarks() {
        if (!Chance(2)) {
            return "";
        }
        var pool = new[] {
            """{"type":"strong"}""", """{"type":"em"}""", """{"type":"underline"}""", """{"type":"strike"}""", """{"type":"code"}""",
            """{"type":"link","attrs":{"href":"https://example.com/x"}}""", """{"type":"link","attrs":{"href":"https://example.com/a b"}}""",
            """{"type":"link","attrs":{"href":"javascript:alert(1)"}}""", """{"type":"textColor","attrs":{"color":"#ff5630"}}""",
            """{"type":"textColor","attrs":{"color":"red"}}""", """{"type":"backgroundColor","attrs":{"color":"#F8E6A0"}}""",
            """{"type":"subsup","attrs":{"type":"sub"}}""", """{"type":"annotation","attrs":{"id":"c1","annotationType":"inlineComment"}}""",
        };
        var marks = Enumerable.Range(0, _random.Next(1, 4)).Select(_ => pool[_random.Next(pool.Length)]);
        return $$""","marks":[{{string.Join(',', marks)}}]""";
    }

    private string LocalId(bool first = false) => Chance(3) ? $"{(first ? "" : ",")}\"localId\":\"{Id("l")}\"" : first ? "\"localId\":\"x\"" : "";

    private string Future() => Chance(6) ? ",\"futureAttr\":{\"v\":[1,2]}" : "";

    private string Id(string prefix) => prefix + (++_ids).ToString(CultureInfo.InvariantCulture);

    private string Many(int min, int max, Func<string> make) {
        var count = _random.Next(min, max + 1);
        var parts = new StringBuilder();
        for (var i = 0; i < count; i++) {
            parts.Append(i == 0 ? "" : ",").Append(make());
        }
        return parts.ToString();
    }

    private bool Chance(int oneIn) => _random.Next(oneIn) == 0;

    private string Pick(params string[] values) => values[_random.Next(values.Length)];
}
