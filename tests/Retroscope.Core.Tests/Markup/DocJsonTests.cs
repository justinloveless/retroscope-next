using System.Text.Json;
using Retroscope.Core.Markup;

namespace Retroscope.Core.Tests.Markup;

[Trait("Category", "Markup")]
public sealed class DocJsonTests {
    [Fact]
    public void Serializer_uses_the_wire_shape_through_the_converter() {
        var doc = MarkupParser.Parse("**hi**", MarkupDialect.Retro);

        Assert.Equal(DocJson.Serialize(doc), JsonSerializer.Serialize(doc));
        Assert.Equal("""{"t":"doc","c":[{"t":"p","c":[{"t":"b","c":[{"t":"text","v":"hi"}]}]}]}""", DocJson.Serialize(doc));
    }

    [Fact]
    public void Docs_are_never_read_back() {
        Assert.Throws<NotSupportedException>(() => JsonSerializer.Deserialize<Doc>("""{"t":"doc","c":[]}"""));
    }

    [Fact]
    public void Adf_only_nodes_have_a_wire_shape() {
        var doc = new Doc([
            new DocPanel(DocPanelKind.Warning, [new DocParagraph([new DocStyled(DocStyle.Underline, [new DocText("u")])])]),
            new DocTable([new DocTableRow([new DocTableCell(true, []), new DocTableCell(false, [])])]),
            new DocExpand("More", []),
            new DocTaskList([new DocTaskItem(true, [new DocStatus("DONE", DocStatusColor.Green), new DocAttachment("10001", "shot.png")])]),
            new DocIsland("abc", [new DocRule()]),
        ]);

        Assert.Equal(
            """{"t":"doc","c":[""" +
            """{"t":"panel","kind":"warning","c":[{"t":"p","c":[{"t":"u","c":[{"t":"text","v":"u"}]}]}]},""" +
            """{"t":"table","c":[{"t":"tr","c":[{"t":"th","c":[]},{"t":"td","c":[]}]}]},""" +
            """{"t":"expand","title":"More","c":[]},""" +
            """{"t":"tasks","c":[{"t":"task","done":true,"c":[{"t":"status","v":"DONE","color":"green"},{"t":"attachment","id":"10001","alt":"shot.png"}]}]},""" +
            """{"t":"island","id":"abc","c":[{"t":"hr"}]}]}""",
            DocJson.Serialize(doc));
    }

    [Fact]
    public void Display_only_nodes_and_attributes_have_a_wire_shape() {
        var doc = new Doc([
            new DocParagraph([
                new DocMarked(DocMarkKind.TextColor, [new DocText("c")], "blue-bolder"),
                new DocMarked(DocMarkKind.Background, [new DocMarked(DocMarkKind.Sup, [new DocText("2")])], "#a1b2c3"),
                new DocLink("https://example.com", [new DocText("l")], "tip"),
                new DocUser("@Ann"),
                new DocDate(new DateOnly(2026, 1, 2)),
                new DocStatus("OK", DocStatusColor.Green, bold: true),
                new DocCard("https://example.com/c", block: false),
                new DocFile("10002", "spec.pdf"),
            ], new DocBlockFormat(DocAlign.Center, 2)),
            new DocHeading(3, [new DocText("h")], new DocBlockFormat(DocAlign.End, 0)),
            new DocPanel(DocPanelKind.Custom, [], "green-subtlest", "!"),
            new DocTable([new DocTableRow([new DocTableCell(false, [], new DocCellFormat(2, 3, [100, 50], "red-subtler"))])], DocLayout.Wide, numbered: true, width: 640),
            new DocLayoutSection([new DocLayoutColumn(33.33, [])]),
            new DocMedia(DocLayout.WrapRight, 320, DocWidthUnit.Pixel, [new DocAttachment("10001", "a.png", 800, 600)], [new DocText("cap")]),
            new DocMediaGroup([new DocAttachment("10001", "a.png")]),
            new DocDecisionList([new DocDecisionItem(true, [new DocText("d")])]),
            new DocTaskList([new DocTaskList([new DocTaskItem(false, [])])]),
        ]);

        Assert.Equal(
            """{"t":"doc","c":[""" +
            """{"t":"p","align":"center","indent":2,"c":[{"t":"fg","color":"blue-bolder","c":[{"t":"text","v":"c"}]},""" +
            """{"t":"bg","color":"#a1b2c3","c":[{"t":"sup","c":[{"t":"text","v":"2"}]}]},""" +
            """{"t":"a","href":"https://example.com","title":"tip","c":[{"t":"text","v":"l"}]},""" +
            """{"t":"user","v":"@Ann"},{"t":"date","v":"2026-01-02"},{"t":"status","v":"OK","color":"green","bold":true},""" +
            """{"t":"card","href":"https://example.com/c"},{"t":"file","id":"10002","name":"spec.pdf"}]},""" +
            """{"t":"h","level":3,"align":"end","c":[{"t":"text","v":"h"}]},""" +
            """{"t":"panel","kind":"custom","color":"green-subtlest","icon":"!","c":[]},""" +
            """{"t":"table","layout":"wide","numbered":true,"width":640,"c":[{"t":"tr","c":[{"t":"td","colspan":2,"rowspan":3,"colwidth":[100,50],"bg":"red-subtler","c":[]}]}]},""" +
            """{"t":"layout","c":[{"t":"col","width":33.33,"c":[]}]},""" +
            """{"t":"media","layout":"wrap-right","width":320,"unit":"px","caption":[{"t":"text","v":"cap"}],"c":[{"t":"attachment","id":"10001","alt":"a.png","w":800,"h":600}]},""" +
            """{"t":"files","c":[{"t":"attachment","id":"10001","alt":"a.png"}]},""" +
            """{"t":"decisions","c":[{"t":"decision","done":true,"c":[{"t":"text","v":"d"}]}]},""" +
            """{"t":"tasks","c":[{"t":"tasks","c":[{"t":"task","done":false,"c":[]}]}]}]}""",
            DocJson.Serialize(doc));
    }

    [Fact]
    public void Writer_refuses_nodes_that_have_no_markup_form() {
        var doc = new Doc([new DocPanel(DocPanelKind.Info, [])]);

        Assert.Throws<ArgumentException>(() => MarkupWriter.Write(doc));
    }

    [Fact]
    public void Writer_refuses_display_only_attributes() {
        Assert.Throws<ArgumentException>(() => MarkupWriter.Write(new Doc([new DocParagraph([new DocText("a")], new DocBlockFormat(DocAlign.Center, 0))])));
        Assert.Throws<ArgumentException>(() => MarkupWriter.Write(new Doc([new DocHeading(1, [new DocText("a")], new DocBlockFormat(DocAlign.Start, 1))])));
        Assert.Throws<ArgumentException>(() => MarkupWriter.Write(new Doc([new DocParagraph([new DocLink("https://a.example", [new DocText("a")], "t")])])));
        Assert.Throws<ArgumentException>(() => MarkupWriter.Write(new Doc([new DocParagraph([new DocMarked(DocMarkKind.Sub, [new DocText("a")])])])));
    }
}
