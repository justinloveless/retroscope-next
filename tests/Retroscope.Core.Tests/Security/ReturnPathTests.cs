using FsCheck.Xunit;
using Retroscope.Core.Security;

namespace Retroscope.Core.Tests.Security;

[Trait("Category", "Security")]
public sealed class ReturnPathTests {
    private static readonly Uri PublicUrl = new("https://retroscope.example.com/");

    [Theory]
    [InlineData("/", "/")]
    [InlineData("/dashboard", "/dashboard")]
    [InlineData("/teams/6f9619ff-8b86-d011-b42d-00c04fc964ff/settings", "/teams/6f9619ff-8b86-d011-b42d-00c04fc964ff/settings")]
    [InlineData("/retro/ABCD234567?tab=actions#card-1", "/retro/ABCD234567?tab=actions#card-1")]
    [InlineData("/a/../b", "/b")]
    [InlineData("/search?q=a%2Fb", "/search?q=a%2Fb")]
    public void Accepts_same_origin_paths(string value, string expected) {
        Assert.True(ReturnPath.TryValidate(PublicUrl, value, out var path));
        Assert.Equal(expected, path);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("//evil")]
    [InlineData("//evil.com/path")]
    [InlineData("///evil.com")]
    [InlineData("/\\evil")]
    [InlineData("/\\/evil.com")]
    [InlineData("\\\\evil.com")]
    [InlineData("/%09/evil.com")]
    [InlineData("/%0a/evil.com")]
    [InlineData("/%0d%0aLocation:%20https://evil.com")]
    [InlineData("/%2F/evil.com")]
    [InlineData("/%2f%2fevil.com")]
    [InlineData("/%5Cevil.com")]
    [InlineData("/%255C/evil.com")]
    [InlineData("/%25252F/evil.com")]
    [InlineData("/\t/evil.com")]
    [InlineData("/\u00a0/evil.com")]
    [InlineData(" /dashboard")]
    [InlineData("/dashboard\n")]
    [InlineData("/\u2028/evil.com")]
    [InlineData("/..//evil.com")]
    [InlineData("/./..//evil.com")]
    [InlineData("/%2e%2e//evil.com")]
    [InlineData("https://evil.com/")]
    [InlineData("https://retroscope.example.com/dashboard")]
    [InlineData("http://retroscope.example.com/")]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("dashboard")]
    [InlineData("?next=//evil.com")]
    [InlineData("#/evil")]
    public void Refuses(string? value) {
        Assert.False(ReturnPath.TryValidate(PublicUrl, value, out var path));
        Assert.Null(path);
    }

    [Fact]
    public void Refuses_overlong_values() {
        Assert.False(ReturnPath.TryValidate(PublicUrl, "/" + new string('a', ReturnPath.MaxLength), out _));
    }

    [Property(MaxTest = 2000)]
    public bool Anything_accepted_is_a_same_origin_absolute_path(string value) {
        if (!ReturnPath.TryValidate(PublicUrl, value, out var path)) {
            return true;
        }

        var resolved = new Uri(PublicUrl, path);
        return path.StartsWith('/')
            && !path.StartsWith("//", StringComparison.Ordinal)
            && !path.Contains('\\', StringComparison.Ordinal)
            && !path.Any(c => char.IsControl(c) || char.IsWhiteSpace(c))
            && resolved.Host == PublicUrl.Host
            && resolved.Scheme == PublicUrl.Scheme
            && resolved.Port == PublicUrl.Port;
    }

    [Property(MaxTest = 500)]
    public bool Prefixing_a_slash_payload_never_escapes(string suffix) {
        foreach (var prefix in new[] { "//", "/\\", "/%2f", "/%5c", "/%09/", "\\" }) {
            if (ReturnPath.TryValidate(PublicUrl, prefix + suffix, out var path) && path.StartsWith("//", StringComparison.Ordinal)) {
                return false;
            }
        }

        return true;
    }
}
