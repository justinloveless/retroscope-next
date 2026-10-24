using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Retroscope.Core.Adf;
using static Retroscope.Core.Tests.Adf.AdfEditProjectionTests;

namespace Retroscope.Core.Tests.Adf;

[Trait("Category", "Adf")]
public sealed partial class AdfWriteTests {
    private const string Empty = """{"type":"doc","version":1,"content":[]}""";
    private const string Extension = """{"type":"extension","attrs":{"extensionKey":"toc","extensionType":"com.atlassian.macro"}}""";
    private const string Bodied = """{"type":"bodiedExtension","attrs":{"extensionKey":"b","extensionType":"x"},"content":[{"type":"paragraph"}]}""";

    private static readonly JsonElement Hostile = LoadHostile();

    public static TheoryData<string> HostileWrites() {
        var data = new TheoryData<string>();
        foreach (var write in Hostile.EnumerateArray()) {
            data.Add(write.GetProperty("name").GetString()!);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(HostileWrites))]
    public void Hostile_writes_are_refused_with_a_code_and_a_path(string name) {
        var write = Hostile.EnumerateArray().Single(w => w.GetProperty("name").GetString() == name);
        var bytes = write.TryGetProperty("writeText", out var text)
            ? Encoding.UTF8.GetBytes(text.GetString()!)
            : Encoding.UTF8.GetBytes(write.GetProperty("write").GetRawText());

        var (ok, _, errors, missing) = Write(Empty, bytes);

        Assert.False(ok);
        Assert.Empty(missing);
        Assert.Equal((write.GetProperty("code").GetString(), write.GetProperty("at").GetString()), (errors[0].Code, errors[0].Path));
    }

    [Fact]
    public void Generated_hostile_shapes_are_refused() {
        var deep = new StringBuilder();
        for (var i = 0; i < 10_000; i++) {
            deep.Append("""{"type":"blockquote","content":[""");
        }
        Assert.Equal(AdfWriteCodes.InvalidJson, Write(Empty, Encoding.UTF8.GetBytes(deep.ToString())).Errors[0].Code);

        // The most rules that fit under the size cap.
        var wide = Doc(string.Join(',', Enumerable.Repeat("""{"type":"rule"}""", 60_000)));
        Assert.Equal(AdfWriteCodes.TooManyNodes, Write(Empty, wide).Errors[0].Code);

        var big = Doc($$"""{"type":"paragraph","content":[{"type":"text","text":"{{new string('x', 1_100_000)}}"}]}""");
        Assert.Equal(AdfWriteCodes.TooLarge, Write(Empty, big).Errors[0].Code);

        byte[] rawSurrogate = [.. Encoding.UTF8.GetBytes("""{"type":"doc","version":1,"content":[{"type":"paragraph","content":[{"type":"text","text":"a"""), 0xED, 0xA0, 0x80, .. "\"}]}]}"u8];
        Assert.Equal(AdfWriteCodes.InvalidText, Write(Empty, rawSurrogate).Errors[0].Code);
    }

    [Fact]
    public void Every_editable_kind_can_be_written_new() {
        var adf = Doc(
            """{"type":"heading","attrs":{"level":1,"localId":"a1"},"marks":[{"type":"indentation","attrs":{"level":1}}],"content":[{"type":"text","text":"T"}]}""",
            """{"type":"paragraph","marks":[{"type":"alignment","attrs":{"align":"end"}}],"content":[{"type":"text","text":"a","marks":[{"type":"em"},{"type":"strong"},{"type":"underline"},{"type":"strike"},{"type":"subsup","attrs":{"type":"sup"}},{"type":"textColor","attrs":{"color":"#0747A6"}},{"type":"backgroundColor","attrs":{"color":"#F8E6A0"}},{"type":"link","attrs":{"href":"https://ok.example/a%20b","title":"T"}}]},{"type":"text","text":"c","marks":[{"type":"code"},{"type":"link","attrs":{"href":"mailto:a@b.example"}}]},{"type":"hardBreak","attrs":{"text":"\n"}},{"type":"emoji","attrs":{"shortName":":smile:","id":"1f604","text":"😄"}},{"type":"date","attrs":{"timestamp":"0"}},{"type":"status","attrs":{"text":"DONE","color":"green","style":"bold"}},{"type":"inlineCard","attrs":{"url":"https://ok.example/c"}}]}""",
            """{"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph"},{"type":"taskList","attrs":{"localId":"tl"},"content":[{"type":"taskItem","attrs":{"localId":"t1","state":"TODO"}}]}]}]}""",
            """{"type":"orderedList","attrs":{"order":0},"content":[{"type":"listItem","content":[{"type":"codeBlock","attrs":{"language":"c++","wrap":true,"hideLineNumbers":false,"uniqueId":"u"},"content":[{"type":"text","text":"a\r\nb"}]}]}]}""",
            """{"type":"blockquote","content":[{"type":"paragraph"}]}""",
            """{"type":"rule","attrs":{"localId":"r"}}""",
            """{"type":"panel","attrs":{"panelType":"custom","panelColor":"#E3FCEF","panelIcon":":smile:","panelIconId":"1f604","panelIconText":"😄"},"content":[{"type":"heading","attrs":{"level":3}}]}""",
            """{"type":"expand","attrs":{"title":"E"},"content":[{"type":"table","attrs":{"layout":"default","width":760,"isNumberColumnEnabled":false,"displayMode":"fixed"},"content":[{"type":"tableRow","content":[{"type":"tableHeader","attrs":{"colspan":2,"rowspan":1,"colwidth":[10,20],"background":"#DEEBFF","valign":"top"},"content":[{"type":"nestedExpand","attrs":{"title":"N"},"content":[{"type":"paragraph"}]}]}]}]}]}""",
            """{"type":"layoutSection","content":[{"type":"layoutColumn","attrs":{"width":33.33,"valign":"middle"},"content":[{"type":"decisionList","attrs":{"localId":"dl"},"content":[{"type":"decisionItem","attrs":{"localId":"d1","state":"UNDECIDED"}}]}]},{"type":"layoutColumn","attrs":{"width":66.67},"content":[{"type":"embedCard","attrs":{"url":"https://ok.example/e","layout":"wide","width":50,"originalWidth":800,"originalHeight":600}}]}]}""",
            """{"type":"blockCard","attrs":{"url":"https://ok.example/b"}}""");

        var (ok, written, errors, _) = Write(Empty, adf);

        Assert.True(ok, Describe(errors));
        AssertCanonical(adf, written!);
    }

    [Fact]
    public void Output_is_rebuilt_not_echoed() {
        var (ok, written, _, _) = Write(Empty, Doc("""{ "content" : [ { "text" : "<b>café</b>", "type" : "text" } ] , "type" : "paragraph" }"""));

        Assert.True(ok);
        Assert.Equal("""{"type":"doc","version":1,"content":[{"type":"paragraph","content":[{"type":"text","text":"<b>café</b>"}]}]}""", written);
    }

    [Fact]
    public void Opaque_tokens_are_restored_byte_for_byte_and_may_move_where_their_type_may_stand() {
        const string Spaced = """{ "type" : "extension", "attrs" : { "extensionKey" : "toc" , "extensionType" : "x" } }""";
        var @base = Doc(Spaced, """{"type":"blockquote","content":[{"type":"paragraph"}]}""");
        var view = Project(@base);
        var token = Token(view, 0);

        var (ok, written, _, _) = Write(view, Doc("""{"type":"paragraph"}""", $$"""{"type":"blockquote","content":[{"type":"paragraph"},{{token}}]}"""));

        Assert.True(ok);
        Assert.Contains(Spaced, written, StringComparison.Ordinal);
    }

    [Fact]
    public void An_opaque_token_used_more_often_than_the_base_holds_it_is_refused() {
        var view = Project(Doc(Extension));
        var token = Token(view, 0);

        var errors = Write(view, Doc(token, token)).Errors;

        Assert.Equal([new AdfWriteError(AdfWriteCodes.OpaqueDuplicated, "content.1")], errors);
    }

    [Fact]
    public void An_opaque_token_where_its_original_may_not_stand_is_refused() {
        var view = Project(Doc(Bodied, Extension, """{"type":"blockquote","content":[{"type":"heading","attrs":{"level":1}}]}"""));
        var bodied = Token(view, 0);
        var extension = Token(view, 1);
        var misplacedHeading = JsonNode.Parse(view.Adf)!["content"]![2]!["content"]![0]!.ToJsonString();

        Assert.Equal(AdfWriteCodes.OpaqueMisplaced, Write(view, Doc(Cell(bodied))).Errors[0].Code);
        Assert.Equal(AdfWriteCodes.OpaqueMisplaced, Write(view, Doc($$"""{"type":"paragraph","content":[{{extension}}]}""")).Errors[0].Code);
        Assert.Equal(AdfWriteCodes.OpaqueMisplaced, Write(view, Doc($$"""{"type":"bulletList","content":[{{extension}}]}""")).Errors[0].Code);
        Assert.Equal(AdfWriteCodes.OpaqueMisplaced,
            Write(view, Doc($$"""{"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph"},{{misplacedHeading}}]}]}""")).Errors[0].Code);
        // Where the base had it, or where its type may stand, it is fine.
        Assert.True(Write(view, Doc($$"""{"type":"blockquote","content":[{{misplacedHeading}}]}""")).Ok);
        Assert.True(Write(view, Doc(misplacedHeading, Cell(extension))).Ok);
    }

    [Fact]
    public void Tokens_the_base_does_not_hold_are_reported_as_missing_not_as_errors() {
        var view = Project(Doc(Extension, """{"type":"paragraph","content":[{"type":"text","text":"a","marks":[{"type":"annotation","attrs":{"id":"c","annotationType":"inlineComment"}}]}]}"""));
        var other = Project(Doc("""{"type":"extension","attrs":{"extensionKey":"other","extensionType":"x"}}"""));

        var result = Write(view, Doc(
            Token(other, 0),
            """{"type":"rsOpaque","attrs":{"ref":"AAAAAAAAAAAAAAAA"}}""",
            """{"type":"paragraph","attrs":{"rsKeep":"BBBBBBBBBBBBBBBB"},"content":[{"type":"text","text":"a","marks":[{"type":"rsMark","attrs":{"ref":"CCCCCCCCCCCCCCCC"}}]}]}"""));

        Assert.False(result.Ok);
        Assert.Empty(result.Errors);
        Assert.Equal([other.Opaque[0].Ref, "AAAAAAAAAAAAAAAA", "BBBBBBBBBBBBBBBB", "CCCCCCCCCCCCCCCC"], result.Missing);
    }

    [Fact]
    public void Mark_tokens_restore_the_original_mark_on_text_only() {
        const string Annotation = """{"type":"annotation","attrs":{"annotationType":"inlineComment","id":"c1"}}""";
        var view = Project(Doc($$"""{"type":"paragraph","content":[{"type":"text","text":"anchored","marks":[{{Annotation}}]}]}"""));
        var mark = JsonNode.Parse(view.Adf)!["content"]![0]!["content"]![0]!["marks"]![0]!.ToJsonString();

        var (ok, written, _, _) = Write(view, Doc($$"""{"type":"paragraph","content":[{"type":"text","text":"edited","marks":[{{mark}}]},{"type":"text","text":" more","marks":[{{mark}}]}]}"""));
        Assert.True(ok);
        Assert.Equal(2, Regex.Count(written!, Regex.Escape(Annotation)));

        Assert.Equal(AdfWriteCodes.OpaqueMisplaced, Write(view, Doc($$"""{"type":"paragraph","marks":[{{mark}}]}""")).Errors[0].Code);
        Assert.Equal(AdfWriteCodes.OpaqueMisplaced,
            Write(view, Doc($$"""{"type":"codeBlock","content":[{"type":"text","text":"x","marks":[{{mark}}]}]}""")).Errors[0].Code);
    }

    [Fact]
    public void A_stash_returns_for_attributes_the_client_did_not_set_and_only_on_its_own_node_type() {
        var view = Project(Doc("""{"type":"codeBlock","attrs":{"language":"Protocol Buffers","futureAttr":[1]},"content":[]}"""));
        var keep = (string)JsonNode.Parse(view.Adf)!["content"]![0]!["attrs"]!["rsKeep"]!;

        var kept = Write(view, Doc($$$"""{"type":"codeBlock","attrs":{"rsKeep":"{{{keep}}}"}}"""));
        Assert.True(kept.Ok);
        AssertCanonical(Doc("""{"type":"codeBlock","attrs":{"language":"Protocol Buffers","futureAttr":[1]}}"""), kept.Adf!);

        var overridden = Write(view, Doc($$$"""{"type":"codeBlock","attrs":{"language":"proto","rsKeep":"{{{keep}}}"}}"""));
        AssertCanonical(Doc("""{"type":"codeBlock","attrs":{"language":"proto","futureAttr":[1]}}"""), overridden.Adf!);

        Assert.Equal(
            new AdfWriteError(AdfWriteCodes.OpaqueMisplaced, "content.0.attrs.rsKeep"),
            Write(view, Doc($$$"""{"type":"paragraph","attrs":{"rsKeep":"{{{keep}}}"}}""")).Errors[0]);
    }

    [Fact]
    public void Hrefs_and_status_colours_Jira_wrote_may_stay_but_new_ones_must_be_strict() {
        var view = Project(Doc("""{"type":"paragraph","content":[{"type":"text","text":"a","marks":[{"type":"link","attrs":{"href":"https://ok.example/a b"}}]},{"type":"status","attrs":{"text":"S","color":"#123456"}}]}"""));

        Assert.True(Write(view, view.Adf).Ok);
        Assert.Equal(AdfWriteCodes.InvalidHref, Write(view, Doc("""{"type":"paragraph","content":[{"type":"text","text":"a","marks":[{"type":"link","attrs":{"href":"https://ok.example/c d"}}]}]}""")).Errors[0].Code);
        Assert.True(Write(view, Doc("""{"type":"paragraph","content":[{"type":"status","attrs":{"text":"T","color":"#123456"}}]}""")).Ok);
        Assert.Equal(AdfWriteCodes.InvalidColor, Write(view, Doc("""{"type":"paragraph","content":[{"type":"status","attrs":{"text":"T","color":"#654321"}}]}""")).Errors[0].Code);
    }

    [Fact]
    public void A_ragged_table_Jira_wrote_may_stay_ragged_but_a_new_one_may_not() {
        const string Rows = """{"type":"tableRow","content":[{"type":"tableCell","content":[{"type":"paragraph"}]},{"type":"tableCell","content":[{"type":"paragraph"}]}]},{"type":"tableRow","content":[{"type":"tableCell","content":[{"type":"paragraph"}]}]}""";
        var view = Project(Doc($$"""{"type":"table","attrs":{"localId":"jira"},"content":[{{Rows}}]}"""));

        Assert.True(Write(view, view.Adf).Ok);
        Assert.True(Write(view, Doc($$"""{"type":"table","attrs":{"localId":"jira"},"content":[{{Rows}},{{Rows}}]}""")).Ok);
        Assert.Equal(AdfWriteCodes.TableShape, Write(view, Doc($$"""{"type":"table","attrs":{"localId":"other"},"content":[{{Rows}}]}""")).Errors[0].Code);
        Assert.True(Write(view, Doc("""{"type":"table","content":[{"type":"tableRow","content":[{"type":"tableCell","attrs":{"colspan":2},"content":[{"type":"paragraph"}]}]},{"type":"tableRow","content":[{"type":"tableCell","attrs":{"rowspan":1},"content":[{"type":"paragraph"}]},{"type":"tableCell","content":[{"type":"paragraph"}]}]}]}""")).Ok);
    }

    [Fact]
    public void Mentions_name_accounts_the_description_holds_or_mention_search_returned() {
        const string Known = "5b10ac8d82e05b22cc7d4ef5";
        var view = Project(Doc($$$"""{"type":"paragraph","content":[{"type":"mention","attrs":{"id":"{{{Known}}}","text":"@Ada","accessLevel":"CONTAINER"}}]}"""));
        string Mention(string id, string text) => Doc($$$"""{"type":"paragraph","content":[{"type":"mention","attrs":{"id":"{{{id}}}","text":"{{{text}}}","userType":"DEFAULT"}}]}""");

        Assert.True(Write(view, Mention(Known, "@Ada L.")).Ok);
        Assert.Equal(AdfWriteCodes.UnknownMention, Write(view, Mention("searched-1", "@Grace")).Errors[0].Code);

        var context = new AdfWriteContext(view) { KnownAccount = id => id == "searched-1" ? "Grace Hopper" : null };
        Assert.True(AdfWrite.TryWrite(Encoding.UTF8.GetBytes(Mention("searched-1", "@Forged")), context, out var written, out _, out _));
        Assert.Contains("\"text\":\"@Grace Hopper\"", Encoding.UTF8.GetString(written!), StringComparison.Ordinal);
        Assert.DoesNotContain("Forged", Encoding.UTF8.GetString(written!), StringComparison.Ordinal);
    }

    [Fact]
    public void Media_keep_their_identity_from_the_base_and_new_media_must_be_this_sessions_uploads() {
        var view = Project(Doc("""{"type":"mediaSingle","attrs":{"layout":"center"},"content":[{"type":"media","attrs":{"type":"file","id":"m-1","collection":"","alt":"shot.png","width":100,"height":50}}]}"""));

        var moved = Write(view, Doc("""{"type":"mediaSingle","attrs":{"layout":"align-start","width":300,"widthType":"pixel"},"content":[{"type":"media","attrs":{"type":"file","id":"m-1","collection":"evil","alt":"renamed.png","width":200,"height":100}}]}"""));
        Assert.True(moved.Ok, Describe(moved.Errors));
        AssertCanonical(
            Doc("""{"type":"mediaSingle","attrs":{"layout":"align-start","width":300,"widthType":"pixel"},"content":[{"type":"media","attrs":{"type":"file","id":"m-1","collection":"","alt":"shot.png","width":200,"height":100}}]}"""),
            moved.Adf!);

        var upload = Doc("""{"type":"mediaSingle","attrs":{"layout":"center"},"content":[{"type":"media","attrs":{"type":"file","id":"new-1","collection":"x"}}]}""");
        Assert.Equal(AdfWriteCodes.UnknownMedia, Write(view, upload).Errors[0].Code);
        var context = new AdfWriteContext(view) { Upload = id => id == "new-1" ? new AdfUpload("paste.png", 640, 480) : null };
        Assert.True(AdfWrite.TryWrite(Encoding.UTF8.GetBytes(upload), context, out var written, out _, out _));
        AssertCanonical(
            Doc("""{"type":"mediaSingle","attrs":{"layout":"center"},"content":[{"type":"media","attrs":{"type":"file","id":"new-1","collection":"","alt":"paste.png","width":640,"height":480}}]}"""),
            Encoding.UTF8.GetString(written!));
    }

    [Fact]
    public void External_media_may_only_stay_where_Jira_had_it() {
        var view = Project(Doc("""{"type":"mediaSingle","attrs":{"layout":"center"},"content":[{"type":"media","attrs":{"type":"external","url":"https://img.example/a.png","alt":"a"}}]}"""));

        Assert.True(Write(view, view.Adf).Ok);
        Assert.Equal(AdfWriteCodes.InvalidMedia,
            Write(view, Doc("""{"type":"mediaSingle","attrs":{"layout":"center"},"content":[{"type":"media","attrs":{"type":"external","url":"https://img.example/b.png"}}]}""")).Errors[0].Code);
    }

    [Fact]
    public void Mark_rules_follow_the_schema() {
        string Text(string marks) => Doc($$"""{"type":"paragraph","content":[{"type":"text","text":"x","marks":[{{marks}}]}]}""");

        Assert.Equal(AdfWriteCodes.InvalidMark, Write(Empty, Text("""{"type":"code"},{"type":"strong"}""")).Errors[0].Code);
        Assert.Equal(AdfWriteCodes.InvalidMark, Write(Empty, Text("""{"type":"strong"},{"type":"strong"}""")).Errors[0].Code);
        Assert.Equal(AdfWriteCodes.InvalidMark, Write(Empty, Text("""{"type":"link"}""")).Errors[0].Code);
        Assert.Equal(AdfWriteCodes.InvalidMark,
            Write(Empty, Doc("""{"type":"paragraph","marks":[{"type":"alignment","attrs":{"align":"center"}},{"type":"indentation","attrs":{"level":1}}]}""")).Errors[0].Code);
        Assert.Equal(AdfWriteCodes.InvalidMark,
            Write(Empty, Doc("""{"type":"panel","attrs":{"panelType":"info"},"content":[{"type":"paragraph","marks":[{"type":"alignment","attrs":{"align":"center"}}]}]}""")).Errors[0].Code);
        Assert.Equal(AdfWriteCodes.InvalidMark, Write(Empty, Doc("""{"type":"codeBlock","content":[{"type":"text","text":"x","marks":[{"type":"strong"}]}]}""")).Errors[0].Code);
    }

    [Fact]
    public void Cross_attribute_rules_hold() {
        Assert.Equal(new AdfWriteError(AdfWriteCodes.InvalidAttr, "content.0.attrs.panelColor"),
            Write(Empty, Doc("""{"type":"panel","attrs":{"panelType":"info","panelColor":"#DEEBFF"},"content":[{"type":"paragraph"}]}""")).Errors[0]);
        Assert.Equal(new AdfWriteError(AdfWriteCodes.InvalidAttr, "content.0.attrs.width"),
            Write(Empty, Doc("""{"type":"mediaSingle","attrs":{"layout":"center","width":300},"content":[]}""")).Errors[0]);
        Assert.Equal(AdfWriteCodes.InvalidAttr, Write(Empty, Doc("""{"type":"heading","content":[]}""")).Errors[0].Code);
        Assert.Equal(new AdfWriteError(AdfWriteCodes.InvalidAttr, "content.0.content.0.attrs.state"),
            Write(Empty, Doc("""{"type":"taskList","attrs":{"localId":"l"},"content":[{"type":"taskItem","attrs":{"localId":"t","state":"MAYBE"}}]}""")).Errors[0]);
    }

    [Fact]
    public void Caps_are_the_defaults_or_the_base_when_larger() {
        var longText = Doc($$"""{"type":"paragraph","content":[{"type":"text","text":"{{new string('x', AdfSchema.MaxText + 1)}}"}]}""");
        Assert.Equal(AdfWriteCodes.TooLong, Write(Empty, longText).Errors[0].Code);
        Assert.True(Write(Project(longText), longText).Ok);

        var nested = """{"type":"paragraph"}""";
        for (var i = 0; i <= AdfSchema.MaxDepth / 2; i++) {
            nested = $$"""{"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph"},{{nested}}]}]}""";
        }
        Assert.Contains(Write(Empty, Doc(nested)).Errors, error => error.Code == AdfWriteCodes.TooDeep);
    }

    [Fact]
    public void At_most_twenty_errors_are_reported_and_paths_never_carry_content() {
        var many = Doc(string.Join(',', Enumerable.Range(0, 30).Select(i => $$$"""{"type":"paragraph","content":[{"type":"text","text":"secret {{{i}}}","marks":[{"type":"link","attrs":{"href":"javascript:{{{i}}}"}}]}]}""")));

        var errors = Write(Empty, many).Errors;

        Assert.Equal(AdfWrite.MaxErrors, errors.Count);
        Assert.All(errors, error => Assert.Matches("^[A-Za-z0-9._]*$", error.Path));
        Assert.DoesNotContain(errors, error => error.Path.Contains("secret", StringComparison.Ordinal));
    }

    [Fact]
    public void A_description_that_is_not_a_document_cannot_be_written() {
        using var array = JsonDocument.Parse("""[{"type":"paragraph"}]""");
        var view = AdfEditProjection.Project(array.RootElement, []);

        Assert.Equal(AdfWriteCodes.NotDoc, Write(view, Empty).Errors[0].Code);
    }

    [Fact]
    public void Tokens_and_stashes_cannot_amplify_a_save_past_the_caps() {
        var huge = new string('a', 50_000);
        var marked = Project(Doc($$$"""{"type":"paragraph","content":[{"type":"text","text":"x","marks":[{"type":"annotation","attrs":{"annotationType":"inlineComment","id":"{{{huge}}}"}}]}]}"""));
        var mark = JsonNode.Parse(marked.Adf)!["content"]![0]!["content"]![0]!["marks"]![0]!.ToJsonString();
        var texts = string.Join(',', Enumerable.Repeat($$$"""{"type":"text","text":"x","marks":[{{{mark}}}]}""", 4_000));
        var marks = Doc($$$"""{"type":"paragraph","content":[{{{texts}}}]}""");

        var stashed = Project(Doc($$$"""{"type":"paragraph","attrs":{"futureAttr":"{{{huge}}}"}}"""));
        var keep = (string)JsonNode.Parse(stashed.Adf)!["content"]![0]!["attrs"]!["rsKeep"]!;
        var stashes = Doc(string.Join(',', Enumerable.Repeat($$$"""{"type":"paragraph","attrs":{"rsKeep":"{{{keep}}}"}}""", 4_000)));

        foreach (var (view, save) in (ReadOnlySpan<(AdfEditView, string)>)[(marked, marks), (stashed, stashes)]) {
            var before = GC.GetAllocatedBytesForCurrentThread();
            var (ok, _, errors, _) = Write(view, save);
            var allocated = GC.GetAllocatedBytesForCurrentThread() - before;

            Assert.False(ok);
            Assert.Equal(AdfWriteCodes.TooLarge, errors[^1].Code);
            Assert.True(allocated < 64L * 1024 * 1024, $"allocated {allocated} bytes");
        }
    }

    [Fact]
    public void An_original_may_not_be_moved_so_deep_that_the_description_could_not_be_read_back() {
        var deep = "1";
        for (var i = 0; i < 12; i++) {
            deep = $$$"""{"a":{{{deep}}}}""";
        }
        var view = Project(Doc($$$"""{"type":"paragraph","attrs":{"futureAttr":{{{deep}}}}}"""));
        var keep = (string)JsonNode.Parse(view.Adf)!["content"]![0]!["attrs"]!["rsKeep"]!;
        var nested = $$$"""{"type":"paragraph","attrs":{"rsKeep":"{{{keep}}}"}}""";
        for (var i = 0; i < 13; i++) {
            nested = $$$"""{"type":"bulletList","content":[{"type":"listItem","content":[{"type":"paragraph"},{{{nested}}}]}]}""";
        }

        Assert.True(Write(view, view.Adf).Ok);
        Assert.Equal([AdfWriteCodes.TooDeep], Write(view, Doc(nested)).Errors.Select(error => error.Code));
    }

    [Fact]
    public void A_mark_token_counts_as_a_mark_of_its_kind_unless_Jira_paired_it() {
        var view = Project(Doc("""{"type":"paragraph","content":[{"type":"text","text":"x","marks":[{"type":"link","attrs":{"href":"https://ok.example/","occurrenceKey":"k"}}]}]}"""));
        var token = JsonNode.Parse(view.Adf)!["content"]![0]!["content"]![0]!["marks"]![0]!.ToJsonString();
        string Text(string marks) => Doc($$$"""{"type":"paragraph","content":[{"type":"text","text":"x","marks":[{{{marks}}}]}]}""");

        Assert.True(Write(view, view.Adf).Ok);
        Assert.Equal(AdfWriteCodes.InvalidMark, Write(view, Text($$$"""{{{token}}},{{{token}}}""")).Errors[0].Code);
        Assert.Equal(AdfWriteCodes.InvalidMark, Write(view, Text($$$"""{{{token}}},{"type":"link","attrs":{"href":"https://x.example/"}}""")).Errors[0].Code);
        Assert.Equal(AdfWriteCodes.InvalidMark, Write(view, Text($$$"""{"type":"link","attrs":{"href":"https://x.example/"}},{{{token}}}""")).Errors[0].Code);

        var paired = Project(Doc("""{"type":"paragraph","content":[{"type":"text","text":"x","marks":[{"type":"textColor","attrs":{"color":"#ff5630"}},{"type":"textColor","attrs":{"color":"#ff5630"}}]}]}"""));
        Assert.True(Write(paired, paired.Adf).Ok);
    }

    [Fact]
    public void A_ragged_table_Jira_wrote_excuses_only_one_table() {
        const string Ragged = """{"type":"table","attrs":{"localId":"jira"},"content":[{"type":"tableRow","content":[{"type":"tableCell","content":[{"type":"paragraph"}]},{"type":"tableCell","content":[{"type":"paragraph"}]}]},{"type":"tableRow","content":[{"type":"tableCell","content":[{"type":"paragraph"}]}]}]}""";
        var view = Project(Doc(Ragged));

        Assert.Equal(new AdfWriteError(AdfWriteCodes.TableShape, "content.1.content"), Write(view, Doc(Ragged, Ragged)).Errors[0]);
    }

    [Fact]
    public void The_mention_and_upload_hooks_never_write_a_member_twice() {
        var mention = Project(Doc($$$"""{"type":"paragraph","content":[{"type":"mention","attrs":{"id":"5b10ac8d82e05b22cc7d4ef5","text":"@{{{new string('n', 151)}}}"}}]}"""));
        var mentionKeep = (string)JsonNode.Parse(mention.Adf)!["content"]![0]!["content"]![0]!["attrs"]!["rsKeep"]!;
        var known = new AdfWriteContext(mention) { KnownAccount = id => id == "5b10ac8d82e05b22cc7d4ef6" ? "Bob" : null };
        var save = Doc($$$"""{"type":"paragraph","content":[{"type":"mention","attrs":{"id":"5b10ac8d82e05b22cc7d4ef6","rsKeep":"{{{mentionKeep}}}"}}]}""");
        Assert.True(AdfWrite.TryWrite(Encoding.UTF8.GetBytes(save), known, out var written, out var errors, out _), Describe(errors));
        AssertUniqueMembers(written!);

        var media = Project(Doc($$$"""{"type":"mediaSingle","attrs":{"layout":"center"},"content":[{"type":"media","attrs":{"type":"file","id":"m-1","collection":"{{{new string('c', 300)}}}"}}]}"""));
        var mediaKeep = (string)JsonNode.Parse(media.Adf)!["content"]![0]!["content"]![0]!["attrs"]!["rsKeep"]!;
        var upload = new AdfWriteContext(media) { Upload = id => id == "new-1" ? new AdfUpload("paste.png", 640, 480) : null };
        save = Doc($$$"""{"type":"mediaSingle","attrs":{"layout":"center"},"content":[{"type":"media","attrs":{"type":"file","id":"new-1","rsKeep":"{{{mediaKeep}}}"}}]}""");
        Assert.True(AdfWrite.TryWrite(Encoding.UTF8.GetBytes(save), upload, out written, out errors, out _), Describe(errors));
        AssertUniqueMembers(written!);
    }

    private static void AssertUniqueMembers(byte[] adf) {
        using var document = JsonDocument.Parse(adf, new JsonDocumentOptions { AllowDuplicateProperties = false });
    }

    private static string Cell(string content) =>
        $$"""{"type":"table","content":[{"type":"tableRow","content":[{"type":"tableCell","content":[{{content}}]}]}]}""";

    private static string Token(AdfEditView view, int index) => JsonNode.Parse(view.Adf)!["content"]![index]!.ToJsonString();

    private static (bool Ok, string? Adf, IReadOnlyList<AdfWriteError> Errors, IReadOnlyList<string> Missing) Write(string @base, string adf) =>
        Write(Project(@base), Encoding.UTF8.GetBytes(adf));

    private static (bool Ok, string? Adf, IReadOnlyList<AdfWriteError> Errors, IReadOnlyList<string> Missing) Write(string @base, byte[] adf) =>
        Write(Project(@base), adf);

    private static (bool Ok, string? Adf, IReadOnlyList<AdfWriteError> Errors, IReadOnlyList<string> Missing) Write(AdfEditView view, string adf) =>
        Write(view, Encoding.UTF8.GetBytes(adf));

    private static (bool Ok, string? Adf, IReadOnlyList<AdfWriteError> Errors, IReadOnlyList<string> Missing) Write(AdfEditView view, byte[] adf) {
        var ok = AdfWrite.TryWrite(adf, new AdfWriteContext(view), out var written, out var errors, out var missing);
        return (ok, written is null ? null : Encoding.UTF8.GetString(written), errors, missing);
    }

    private static string Describe(IReadOnlyList<AdfWriteError> errors) => string.Join(", ", errors.Select(e => $"{e.Code}@{e.Path}"));

    private static JsonElement LoadHostile() {
        using var document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AdfFixtures.Directory, "hostile.json")));
        return document.RootElement.GetProperty("writes").Clone();
    }
}
