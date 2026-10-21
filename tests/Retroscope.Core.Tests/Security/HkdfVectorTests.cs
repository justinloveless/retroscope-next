using Retroscope.Core.Security;

namespace Retroscope.Core.Tests.Security;

// RFC 5869 appendix A, SHA-256 cases (A.1-A.3), fetched from rfc-editor.org.
[Trait("Category", "Security")]
public sealed class HkdfVectorTests {
    public static TheoryData<string, string, string, int, string> Vectors => new() {
        {
            "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b",
            "000102030405060708090a0b0c",
            "f0f1f2f3f4f5f6f7f8f9",
            42,
            "3cb25f25faacd57a90434f64d0362f2a2d2d0a90cf1a5a4c5db02d56ecc4c5bf34007208d5b887185865"
        },
        {
            Range(0x00, 80),
            Range(0x60, 80),
            Range(0xb0, 80),
            82,
            "b11e398dc80327a1c8e7f78c596a49344f012eda2d4efad8a050cc4c19afa97c59045a99cac7827271cb41c65e590e09"
                + "da3275600c2f09b8367793a9aca3db71cc30c58179ec3e87c14c01d5c1f3434f1d87"
        },
        {
            "0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b0b",
            "",
            "",
            42,
            "8da4e775a563c18f715f802a063c5a31b8a11f5c5ee1879ec3454e5f3c738d2d9d201395faa4b61a96c8"
        },
    };

    [Theory]
    [MemberData(nameof(Vectors))]
    public void Derive_matches_rfc_5869(string ikm, string salt, string info, int length, string okm) {
        var output = Keyring.Derive(Convert.FromHexString(ikm), Convert.FromHexString(salt), Convert.FromHexString(info), length);

        Assert.Equal(okm, Convert.ToHexStringLower(output));
    }

    private static string Range(int start, int count) =>
        Convert.ToHexStringLower(Enumerable.Range(start, count).Select(i => (byte)i).ToArray());
}
