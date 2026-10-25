using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using System.Text.RegularExpressions;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Tests.Scaffold;

namespace Retroscope.Core.Tests.Protocol;

/// <summary>
/// Checks the catalog and generates <c>web/src/net/protocol.gen.js</c> and <c>docs/protocol.md</c> from it.
/// A stale committed file is rewritten and the test fails, so running the suite once refreshes both and
/// CI (which runs it on a clean checkout and then <c>git diff --exit-code</c>) catches a forgotten commit.
/// </summary>
[Trait("Category", "Protocol")]
public sealed partial class ProtocolCatalogTests {
    private const string JsPath = "web/src/net/protocol.gen.js";
    private const string DocsPath = "docs/protocol.md";

    private static readonly (string Type, string Direction, string Members, string Notes)[] Frames = [
        ("hello", "client", "v, cid, subs:[{r, e?, s?}]", "First frame; resumes every room at once."),
        ("sub", "client", "r, e?, s?", "Subscribe (idempotent); `e`/`s` resume from a known point."),
        ("unsub", "client", "r", ""),
        ("cmd", "client", "r, q, k, d", "`q` strictly increases per `cid`; `d` is the payload of command `k`."),
        ("ping", "client", "c", "Clock sample only (liveness is protocol PING/PONG)."),
        ("welcome", "server", "conn, me:{pid, kind, name, avatar?}, n", "`n` seeds the clock offset."),
        ("snap", "server", "r, e, s, lastQ, d, o?", "`d` is the room snapshot, `o` the per-viewer overlay (poker, board)."),
        ("replay", "server", "r, e, from, lastQ, txs:[{s, ev}]", "Missed tx frames; empty `txs` means current."),
        ("tx", "server", "r, s, ev:[{k, d}]", "One committed command or internal transition; `ev` may be empty."),
        ("ack", "server", "r, q, s, dup?", "Sent after the tx frame it produced."),
        ("nack", "server", "r, q, code, retry, msg?, d?", "`retry` follows from `code`; `stale` carries `d.current`."),
        ("x", "server", "r, k, d", "Unsequenced, idempotent cache fill."),
        ("pong", "server", "c, n", "Clock offset estimation."),
        ("kick", "server", "r, code", "The room is dropped for this connection."),
        ("bye", "server", "code, retryMs", "Sent before a graceful close; reconnect after `retryMs`."),
    ];

    private static readonly Type[] WireEnums = [
        typeof(RoomKind), typeof(Room.PrincipalKind), typeof(Room.TeamRole), typeof(RoundState), typeof(VoteStatus),
        typeof(PointerCause), typeof(JiraSubmitStatus), typeof(RetroStage), typeof(TimerState), typeof(ColumnColor),
    ];

    [Fact]
    public void Every_concrete_payload_type_has_exactly_one_kind() {
        // Wire types live in Core/Protocol; a domain may keep its own internal subtypes elsewhere.
        var payloadBases = new[] { typeof(Command), typeof(WireEvent), typeof(Unsequenced) };
        var concrete = typeof(Catalog).Assembly.GetTypes()
            .Where(x => x.Namespace == typeof(Catalog).Namespace)
            .Where(x => !x.IsAbstract && payloadBases.Any(b => b.IsAssignableFrom(x)))
            .ToHashSet();

        Assert.Equal(concrete.OrderBy(x => x.Name), Catalog.Entries.Select(x => x.Type).OrderBy(x => x.Name));
        Assert.Equal(Catalog.Entries.Count, Catalog.Entries.Select(x => x.Kind).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Kinds_are_dotted_lower_camel_names_that_match_their_room() {
        foreach (var entry in Catalog.Entries) {
            Assert.Matches(KindPattern(), entry.Kind);
            if (entry.Direction == KindDirection.Command) {
                Assert.True(
                    entry.Scope is RoomScope.Poker or RoomScope.Board or RoomScope.Team,
                    $"{entry.Kind} must target exactly one room");
                var expectedBase = entry.Scope switch {
                    RoomScope.Poker => typeof(PokerCommand),
                    RoomScope.Board => typeof(RetroCommand),
                    _ => typeof(TeamCommand),
                };
                Assert.True(expectedBase.IsAssignableFrom(entry.Type), $"{entry.Kind} must derive from {expectedBase.Name}");
                var prefix = entry.Scope == RoomScope.Board ? "retro." : entry.Scope == RoomScope.Poker ? "poker." : "team.";
                Assert.StartsWith(prefix, entry.Kind, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void Ephemeral_kinds_are_the_ones_the_design_names() {
        var ephemeral = Catalog.Entries.Where(x => x.Ephemeral).Select(x => x.Kind).Order(StringComparer.Ordinal);

        Assert.Equal(
            [
                "poker.spotlight.move", "retro.focus.set", "retro.timer.pause", "retro.timer.reset", "retro.timer.resume",
                "retro.timer.setFlags", "retro.timer.start",
            ],
            ephemeral);
    }

    [Fact]
    public void Retryable_nack_codes_are_the_ones_the_design_names() {
        var retryable = Enum.GetValues<NackCode>().Where(x => x.IsRetryable());

        Assert.Equal([NackCode.RateLimited, NackCode.ServerBusy, NackCode.JiraUnavailable], retryable);
    }

    [Fact]
    public void Generation_is_deterministic() {
        Assert.Equal(GenerateJs(), GenerateJs());
        Assert.Equal(GenerateDocs(), GenerateDocs());
    }

    [Fact]
    public void Generated_js_is_current() {
        AssertCurrent(JsPath, GenerateJs());
    }

    [Fact]
    public void Generated_docs_are_current() {
        AssertCurrent(DocsPath, GenerateDocs());
    }

    public static string GenerateJs() {
        var js = new StringBuilder();
        js.Append("// GENERATED from src/Retroscope.Core/Protocol/Catalog.cs by ProtocolCatalogTests.GenerateJs. Do not edit.\n");
        js.Append("// Regenerate: dotnet test tests/Retroscope.Core.Tests --filter Category=Protocol\n\n");
        js.Append($"export const PROTOCOL_VERSION = {Catalog.ProtocolVersion};\n");
        js.Append($"export const MAX_FRAME_BYTES = {WireParser.MaxFrameBytes};\n\n");

        js.Append("/** Command kind → target room, payload members, required members, and whether it is ephemeral. */\n");
        js.Append("export const COMMANDS = Object.freeze({\n");
        foreach (var entry in Catalog.Entries.Where(x => x.Direction == KindDirection.Command)) {
            js.Append($"  {Str(entry.Kind)}: Object.freeze({{ room: {Str(RoomNames(entry.Scope)[0])}, ");
            js.Append($"fields: {Array(Members(entry.TypeInfo).Select(x => x.Name))}, ");
            js.Append($"required: {Array(Members(entry.TypeInfo).Where(WireSamples.IsRequired).Select(x => x.Name))}, ");
            js.Append($"ephemeral: {(entry.Ephemeral ? "true" : "false")} }}),\n");
        }
        js.Append("});\n\n");

        js.Append("/** Event kind → rooms that emit it and its payload members. */\n");
        js.Append("export const EVENTS = Object.freeze({\n");
        foreach (var entry in Catalog.Entries.Where(x => x.Direction == KindDirection.Event)) {
            js.Append($"  {Str(entry.Kind)}: Object.freeze({{ rooms: {Array(RoomNames(entry.Scope))}, ");
            js.Append($"fields: {Array(Members(entry.TypeInfo).Select(x => x.Name))} }}),\n");
        }
        js.Append("});\n\n");

        js.Append("/** Unsequenced (`x` frame) kind → rooms and payload members. */\n");
        js.Append("export const UNSEQUENCED = Object.freeze({\n");
        foreach (var entry in Catalog.Entries.Where(x => x.Direction == KindDirection.Unsequenced)) {
            js.Append($"  {Str(entry.Kind)}: Object.freeze({{ rooms: {Array(RoomNames(entry.Scope))}, ");
            js.Append($"fields: {Array(Members(entry.TypeInfo).Select(x => x.Name))} }}),\n");
        }
        js.Append("});\n\n");

        js.Append("/** Kinds the outbox drops instead of resending after 10 s offline. */\n");
        js.Append($"export const EPHEMERAL_KINDS = Object.freeze({Array(Catalog.Entries.Where(x => x.Ephemeral).Select(x => x.Kind))});\n\n");

        js.Append("/** Nack code → whether the client keeps the outbox entry and retries. */\n");
        js.Append("export const NACK = Object.freeze({\n");
        foreach (var code in Enum.GetValues<NackCode>()) {
            js.Append($"  {WireName(code)}: Object.freeze({{ retry: {(code.IsRetryable() ? "true" : "false")} }}),\n");
        }
        js.Append("});\n\n");

        js.Append($"export const KICK_CODES = Object.freeze({Array(Enum.GetValues<KickCode>().Select(x => WireName(x)))});\n");
        js.Append($"export const BYE_CODES = Object.freeze({Array(Enum.GetValues<ByeCode>().Select(x => WireName(x)))});\n\n");

        js.Append("/** WebSocket close codes. */\n");
        js.Append("export const CLOSE = Object.freeze({\n");
        foreach (var (name, code) in CloseCodes.All) {
            js.Append($"  {ScreamingSnake(name)}: {code},\n");
        }
        js.Append("});\n\n");

        js.Append("/** Wire values of every enum used in payloads, in declaration order. */\n");
        js.Append("export const ENUMS = Object.freeze({\n");
        foreach (var type in WireEnums) {
            js.Append($"  {char.ToLowerInvariant(type.Name[0])}{type.Name[1..]}: Object.freeze({Array(EnumValues(type))}),\n");
        }
        js.Append("});\n\n");

        js.Append("export function isRetryable(code) {\n");
        js.Append("  return NACK[code]?.retry === true;\n");
        js.Append("}\n");
        return js.ToString();
    }

    public static string GenerateDocs() {
        var shapes = new SortedDictionary<string, JsonTypeInfo>(StringComparer.Ordinal);
        var md = new StringBuilder();
        md.Append("# Retroscope wire protocol\n\n");
        md.Append("<!-- GENERATED from src/Retroscope.Core/Protocol/Catalog.cs by ProtocolCatalogTests.GenerateDocs. Do not edit. -->\n\n");
        md.Append($"Protocol version {Catalog.ProtocolVersion}. One same-origin WebSocket per tab at `GET /ws`; every frame is one ");
        md.Append($"UTF-8 JSON object of at most {WireParser.MaxFrameBytes} bytes. Members may appear in any order. Ids are ");
        md.Append("lower-case hyphenated UUID strings, room keys are `poker:<uuid>`, `board:<uuid>` or `team:<uuid>`, epochs are ");
        md.Append("16 hex digits, and timestamps are Unix milliseconds of server time. Members marked `?` are omitted when ");
        md.Append("empty; unknown members are refused. Rules and rationale: DESIGN.md §c.3 and §c.4.\n\n");

        md.Append("## Frames\n\n| `t` | From | Members | Notes |\n|---|---|---|---|\n");
        foreach (var (type, direction, members, notes) in Frames) {
            md.Append($"| `{type}` | {direction} | {members} | {notes} |\n");
        }

        foreach (var (title, direction) in new[] {
            ("Commands", KindDirection.Command), ("Events", KindDirection.Event), ("Unsequenced", KindDirection.Unsequenced),
        }) {
            md.Append($"\n## {title}\n\n| Kind | Rooms | `d` | Summary |\n|---|---|---|---|\n");
            foreach (var entry in Catalog.Entries.Where(x => x.Direction == direction)) {
                var rooms = string.Join(", ", RoomNames(entry.Scope));
                var ephemeral = entry.Ephemeral ? " Ephemeral." : "";
                md.Append($"| `{entry.Kind}` | {rooms} | {Fields(entry.TypeInfo, shapes)} | {entry.Summary}{ephemeral} |\n");
            }
        }

        md.Append("\n## Snapshots\n\n| Room | `d` | `o` |\n|---|---|---|\n");
        foreach (var room in Enum.GetValues<RoomKind>()) {
            var snapshot = Catalog.SnapshotTypeInfo(room);
            var overlay = Catalog.OverlayTypeInfo(room);
            shapes[snapshot.Type.Name] = snapshot;
            if (overlay != null) {
                shapes[overlay.Type.Name] = overlay;
            }
            md.Append($"| {RoomKey.Prefix(room)} | `{snapshot.Type.Name}` | {(overlay is null ? "none" : $"`{overlay.Type.Name}`")} |\n");
        }

        md.Append("\n## Shapes\n");
        var written = new HashSet<string>(StringComparer.Ordinal);
        while (shapes.Keys.FirstOrDefault(x => !written.Contains(x)) is { } name) {
            written.Add(name);
            md.Append($"\n### {name}\n\n| Member | Type |\n|---|---|\n");
            foreach (var field in FieldList(shapes[name], shapes)) {
                md.Append($"| {field.Replace(": ", " | ", StringComparison.Ordinal)} |\n");
            }
        }

        md.Append("\n## Nack codes\n\n| Code | Retry |\n|---|---|\n");
        foreach (var code in Enum.GetValues<NackCode>()) {
            md.Append($"| `{WireName(code)}` | {(code.IsRetryable() ? "yes" : "no")} |\n");
        }

        md.Append("\n## Kick and bye codes\n\n");
        md.Append($"Kick: {string.Join(", ", Enum.GetValues<KickCode>().Select(x => $"`{WireName(x)}`"))}. ");
        md.Append($"Bye: {string.Join(", ", Enum.GetValues<ByeCode>().Select(x => $"`{WireName(x)}`"))}.\n");

        md.Append("\n## Close codes\n\n| Code | Meaning |\n|---|---|\n");
        foreach (var (name, code) in CloseCodes.All) {
            md.Append($"| {code} | {name} |\n");
        }

        md.Append("\n## Enums\n\n| Enum | Values |\n|---|---|\n");
        foreach (var type in WireEnums) {
            md.Append($"| {type.Name} | {string.Join(", ", EnumValues(type).Select(x => $"`{x}`"))} |\n");
        }
        return md.ToString();
    }

    private static void AssertCurrent(string relativePath, string generated) {
        var path = Path.Combine(RepoRoot.Path, relativePath);
        var committed = File.Exists(path) ? File.ReadAllText(path) : null;
        if (committed == generated) {
            return;
        }
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, generated);
        Assert.Fail($"{relativePath} was stale and has been regenerated; review and commit it.");
    }

    private static IEnumerable<JsonPropertyInfo> Members(JsonTypeInfo typeInfo) => typeInfo.Properties;

    private static string Fields(JsonTypeInfo typeInfo, IDictionary<string, JsonTypeInfo> shapes) {
        var fields = FieldList(typeInfo, shapes);
        return fields.Count == 0 ? "`{}`" : string.Join("<br>", fields);
    }

    private static List<string> FieldList(JsonTypeInfo typeInfo, IDictionary<string, JsonTypeInfo> shapes) {
        return Members(typeInfo).Select(property => {
            var required = WireSamples.IsRequired(property);
            var type = Describe(property.PropertyType, shapes);
            if (required && property.IsSetNullable) {
                type += " \\| null";
            }
            return $"`{property.Name}{(required ? "" : "?")}`: {type}";
        }).ToList();
    }

    private static string Describe(Type type, IDictionary<string, JsonTypeInfo> shapes) {
        type = Nullable.GetUnderlyingType(type) ?? type;
        if (type == typeof(string)) {
            return "string";
        }
        if (type == typeof(bool)) {
            return "boolean";
        }
        if (type == typeof(short) || type == typeof(int) || type == typeof(long)) {
            return "integer";
        }
        if (type == typeof(decimal)) {
            return "number";
        }
        if (type == typeof(JsonElement)) {
            return "any";
        }
        if (type == typeof(RoomKey)) {
            return "room key";
        }
        if (type == typeof(Epoch)) {
            return "epoch";
        }
        if (type.GetInterfaces().Any(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IGuidId<>))) {
            return $"uuid ({type.Name})";
        }
        if (type.IsEnum) {
            return type.Name;
        }
        var info = WireSamples.TypeInfo(type);
        switch (info.Kind) {
            case JsonTypeInfoKind.Enumerable:
                return Describe(info.ElementType!, shapes) + "[]";
            case JsonTypeInfoKind.Dictionary:
                return $"{{string: {Describe(info.ElementType!, shapes)}}}";
            default:
                shapes[type.Name] = info;
                return type.Name;
        }
    }

    private static string[] RoomNames(RoomScope scope) {
        return Enum.GetValues<RoomKind>().Where(x => scope.HasFlag(Catalog.ScopeOf(x))).Select(RoomKey.Prefix).ToArray();
    }

    private static IEnumerable<string> EnumValues(Type enumType) {
        return Enum.GetValues(enumType).Cast<object>().Select(x => WireName(x));
    }

    private static readonly JsonSerializerOptions Reflection = new() { TypeInfoResolver = new DefaultJsonTypeInfoResolver() };

    private static string WireName(object value) {
        return JsonSerializer.Deserialize(JsonSerializer.Serialize(value, value.GetType(), Reflection), WireJsonContext.Default.String)!;
    }

    private static string Str(string value) => $"'{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal)}'";

    private static string Array(IEnumerable<string> values) => $"[{string.Join(", ", values.Select(Str))}]";

    private static string ScreamingSnake(string pascal) => SnakeBoundary().Replace(pascal, "$1_$2").ToUpperInvariant();

    [GeneratedRegex("^[a-z]+(\\.[a-z][A-Za-z]*)+$")]
    private static partial Regex KindPattern();

    [GeneratedRegex("([a-z0-9])([A-Z])")]
    private static partial Regex SnakeBoundary();
}
