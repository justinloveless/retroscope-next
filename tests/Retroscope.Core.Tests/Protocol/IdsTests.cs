using Retroscope.Core.Ids;

namespace Retroscope.Core.Tests.Protocol;

[Trait("Category", "Protocol")]
public sealed class IdsTests {
    [Fact]
    public void Generated_room_codes_are_ten_crockford_characters() {
        for (var i = 0; i < 1000; i++) {
            var code = RoomCode.NewRandom();

            Assert.Equal(RoomCode.GeneratedLength, code.Value.Length);
            Assert.All(code.Value, c => Assert.Contains(c, RoomCode.CrockfordAlphabet));
            Assert.True(RoomCode.TryParse(code.Value, out var parsed));
            Assert.Equal(code, parsed);
        }
    }

    [Fact]
    public void Room_code_bytes_map_onto_the_whole_alphabet() {
        Assert.Equal("0123456789", RoomCode.FromRandomBytes([0, 1, 2, 3, 4, 5, 6, 7, 8, 9]).Value);
        Assert.Equal("ABCDEFGHJK", RoomCode.FromRandomBytes([10, 11, 12, 13, 14, 15, 16, 17, 18, 19]).Value);
        Assert.Equal("MNPQRSTVWX", RoomCode.FromRandomBytes([20, 21, 22, 23, 24, 25, 26, 27, 28, 29]).Value);
        Assert.Equal("YZ0YZ0YZ0Z", RoomCode.FromRandomBytes([30, 31, 32, 62, 63, 64, 254, 255, 0, 255]).Value);
        Assert.Throws<ArgumentException>(() => RoomCode.FromRandomBytes([1, 2, 3]));
    }

    [Theory]
    [InlineData("ABC")]
    [InlineData("AB12CD")]
    [InlineData("ab12cd")]
    [InlineData("k3j9x0q1z7w2m")]
    [InlineData("0192f0c1-0000-7000-8000-00000000abcd")]
    [InlineData("1234567890123456789012345678901234567890123456789012345678901234")]
    public void Legacy_codes_parse_verbatim(string text) {
        Assert.True(RoomCode.TryParse(text, out var code));
        Assert.Equal(text, code.Value);
        Assert.Equal(text.ToUpperInvariant(), code.LookupKey);
    }

    [Fact]
    public void Room_codes_differing_only_in_case_are_equal() {
        Assert.True(RoomCode.TryParse("k3j9x0q1z7w2m", out var lower));
        Assert.True(RoomCode.TryParse("K3J9X0Q1Z7W2M", out var upper));
        Assert.True(RoomCode.TryParse("K3J9X0Q1Z7W2N", out var other));

        Assert.Equal(lower, upper);
        Assert.Equal(lower.GetHashCode(), upper.GetHashCode());
        Assert.NotEqual(lower, other);
        Assert.Equal("k3j9x0q1z7w2m", lower.Value);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("AB")]
    [InlineData("ABC DEF")]
    [InlineData("ABC/DEF")]
    [InlineData("ABC_DEF")]
    [InlineData("ÄBCDEF")]
    [InlineData("12345678901234567890123456789012345678901234567890123456789012345")]
    public void Bad_codes_are_refused(string? text) {
        Assert.False(RoomCode.TryParse(text, out _));
    }

    [Theory]
    [InlineData("6ba7b810-9dad-11d1-80b4-00c04fd430c8", "www.example.com", "2ed6657d-e927-568b-95e1-2665a8aea6a2")]
    [InlineData("6ba7b811-9dad-11d1-80b4-00c04fd430c8", "https://www.example.com/", "3d3ed9d2-aa3d-5fa6-90e8-ed662e90f559")]
    [InlineData("6ba7b810-9dad-11d1-80b4-00c04fd430c8", "", "4ebd0208-8328-5d69-8c44-ec50939c0967")]
    public void Uuid_v5_matches_reference_values(string ns, string name, string expected) {
        var id = UuidV5.Create(Guid.Parse(ns), name);

        Assert.Equal(Guid.Parse(expected), id);
        Assert.Equal(5, id.Version);
    }
}
