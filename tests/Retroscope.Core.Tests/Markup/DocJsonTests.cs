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
    public void Writer_refuses_nodes_that_have_no_markup_form() {
        var doc = new Doc([new DocPanel(DocPanelKind.Info, [])]);

        Assert.Throws<ArgumentException>(() => MarkupWriter.Write(doc));
    }
}
