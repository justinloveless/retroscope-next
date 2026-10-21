using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;

namespace Retroscope.Core.Tests.Protocol;

[Trait("Category", "Protocol")]
public sealed class IdConverterTests {
    private const string Canonical = "0192f0c1-0000-7000-8000-00000000abcd";

    public static TheoryData<Type> IdTypes() {
        var data = new TheoryData<Type>();
        foreach (var type in typeof(PrincipalId).Assembly.GetTypes().Where(IsGuidId).OrderBy(x => x.Name, StringComparer.Ordinal)) {
            data.Add(type);
        }
        return data;
    }

    [Theory]
    [MemberData(nameof(IdTypes))]
    public void Every_id_type_carries_its_converter_and_writes_a_bare_lowercase_uuid(Type idType) {
        var converter = idType.GetCustomAttributes(typeof(JsonConverterAttribute), false).Cast<JsonConverterAttribute>().Single();
        Assert.Equal(typeof(GuidIdConverter<>).MakeGenericType(idType), converter.ConverterType);

        var value = Activator.CreateInstance(idType, Guid.Parse(Canonical.ToUpperInvariant()))!;
        var options = new JsonSerializerOptions { TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver() };
        var json = JsonSerializer.Serialize(value, idType, options);

        Assert.Equal($"\"{Canonical}\"", json);
        Assert.Equal(value, JsonSerializer.Deserialize(json, idType, options));
        Assert.Equal(value, JsonSerializer.Deserialize(json.ToUpperInvariant(), idType, options));
    }

    [Fact]
    public void Ids_work_as_dictionary_keys() {
        var a = new PrincipalId(Guid.Parse(Canonical));
        var b = new PrincipalId(Guid.CreateVersion7());
        var map = new Dictionary<PrincipalId, int> { [a] = 1, [b] = 2 };

        var json = JsonSerializer.Serialize(map, KeysContext.Default.DictionaryPrincipalIdInt32);
        var back = JsonSerializer.Deserialize(json, KeysContext.Default.DictionaryPrincipalIdInt32)!;

        Assert.StartsWith($"{{\"{Canonical}\":1,", json, StringComparison.Ordinal);
        Assert.Equal(map, back);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("{\"nope\":1}", KeysContext.Default.DictionaryPrincipalIdInt32));
    }

    [Fact]
    public void Room_keys_work_as_dictionary_keys() {
        var key = new RoomKey(RoomKind.Team, Guid.Parse(Canonical));
        var map = new Dictionary<RoomKey, int> { [key] = 3 };

        var json = JsonSerializer.Serialize(map, KeysContext.Default.DictionaryRoomKeyInt32);

        Assert.Equal($"{{\"team:{Canonical}\":3}}", json);
        Assert.Equal(map, JsonSerializer.Deserialize(json, KeysContext.Default.DictionaryRoomKeyInt32));
    }

    [Theory]
    [InlineData("\"0192f0c1000070008000000000abcd\"")]
    [InlineData("\"0192f0c1000070008000000000000abcd\"")]
    [InlineData("\"{0192f0c1-0000-7000-8000-00000000abcd}\"")]
    [InlineData("\"(0192f0c1-0000-7000-8000-00000000abcd)\"")]
    [InlineData("\" 0192f0c1-0000-7000-8000-00000000abcd\"")]
    [InlineData("\"0192f0c1-0000-7000-8000-00000000abcg\"")]
    [InlineData("\"\"")]
    [InlineData("12")]
    [InlineData("null")]
    [InlineData("{}")]
    [InlineData("\"\\u0030\\u0031\\u0039\\u0032f0c1-0000-7000-8000-00000000abcd-extra-extra-extra-extra-extra-extra-extra-extra-extra-extra-extra-extra-extra-extra-extra-extra-extra-extra-extra-extra-extra-extra-extra-extra-extra-extra-extra-extra-extra-extra-extra-extra\"")]
    public void Non_canonical_ids_are_refused(string json) {
        Assert.ThrowsAny<JsonException>(() => JsonSerializer.Deserialize(json, WireJsonContext.Default.ClientId));
    }

    [Fact]
    public void Escaped_ids_are_unescaped() {
        var json = "\"\\u0030192f0c1-0000-7000-8000-00000000abcd\"";

        Assert.Equal(new ClientId(Guid.Parse(Canonical)), JsonSerializer.Deserialize(json, WireJsonContext.Default.ClientId));
    }

    [Theory]
    [InlineData("poker:" + Canonical, RoomKind.Poker)]
    [InlineData("board:" + Canonical, RoomKind.Board)]
    [InlineData("team:" + Canonical, RoomKind.Team)]
    public void Room_keys_parse_and_format(string text, RoomKind kind) {
        Assert.True(RoomKey.TryParse(text, out var key));
        Assert.Equal(new RoomKey(kind, Guid.Parse(Canonical)), key);
        Assert.Equal(text, key.ToString());
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("poker")]
    [InlineData("poker:")]
    [InlineData("Poker:" + Canonical)]
    [InlineData("retro:" + Canonical)]
    [InlineData("poker:" + Canonical + "x")]
    [InlineData("poker:{" + Canonical + "}")]
    [InlineData("poker:0192f0c1000070008000000000abcd")]
    public void Bad_room_keys_are_refused(string? text) {
        Assert.False(RoomKey.TryParse(text, out _));
    }

    [Fact]
    public void Epochs_are_sixteen_hex_digits() {
        var epoch = new Epoch(0x00ab_cdef_0123_4567UL);

        var json = JsonSerializer.Serialize(epoch, WireJsonContext.Default.Epoch);

        Assert.Equal("\"00abcdef01234567\"", json);
        Assert.Equal(epoch, JsonSerializer.Deserialize(json, WireJsonContext.Default.Epoch));
        Assert.Equal(epoch, JsonSerializer.Deserialize("\"00ABCDEF01234567\"", WireJsonContext.Default.Epoch));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("\"abcdef01234567\"", WireJsonContext.Default.Epoch));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize("12", WireJsonContext.Default.Epoch));
        Assert.NotEqual(Epoch.NewRandom(), Epoch.NewRandom());
    }

    private static bool IsGuidId(Type type) {
        return type.IsValueType && type.GetInterfaces().Any(x => x.IsGenericType && x.GetGenericTypeDefinition() == typeof(IGuidId<>));
    }
}

[JsonSerializable(typeof(Dictionary<PrincipalId, int>))]
[JsonSerializable(typeof(Dictionary<RoomKey, int>))]
internal sealed partial class KeysContext : JsonSerializerContext {
}
