using Retroscope.Core.Security;

namespace Retroscope.Core.Tests.Security;

[Trait("Category", "Security")]
public sealed class JiraHostPolicyTests {
    [Theory]
    [InlineData("acme.atlassian.net", "acme.atlassian.net")]
    [InlineData("ACME.Atlassian.NET", "acme.atlassian.net")]
    [InlineData("https://acme.atlassian.net", "acme.atlassian.net")]
    [InlineData("https://acme.atlassian.net/", "acme.atlassian.net")]
    [InlineData("http://acme.atlassian.net/jira/software/projects/ABC/boards/1", "acme.atlassian.net")]
    [InlineData("acme.atlassian.net:443", "acme.atlassian.net")]
    [InlineData("  https://acme-eu.atlassian.net:443/?x=1#y  ", "acme-eu.atlassian.net")]
    [InlineData("acme.atlassian.net/browse/ABC-1", "acme.atlassian.net")]
    public void Normalizes_atlassian_cloud_hosts(string input, string expected) {
        Assert.True(JiraHostPolicy.AtlassianCloudOnly.TryNormalize(input, out var host));
        Assert.Equal(expected, host);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("atlassian.net")]
    [InlineData(".atlassian.net")]
    [InlineData("atlassian.net.evil.com")]
    [InlineData("acme.atlassian.net.evil.com")]
    [InlineData("https://acme.atlassian.net.evil.com/")]
    [InlineData("evilatlassian.net")]
    [InlineData("acme.atlassian.network")]
    [InlineData("a.b.atlassian.net")]
    [InlineData("acme.atlassian.net.")]
    [InlineData("user@acme.atlassian.net")]
    [InlineData("acme.atlassian.net@evil.com")]
    [InlineData("https://user:pass@acme.atlassian.net")]
    [InlineData("https://evil.com\\@acme.atlassian.net")]
    [InlineData("evil.com\\.atlassian.net")]
    [InlineData("acme%2eatlassian.net")]
    [InlineData("acme.atlassian.net%00.evil.com")]
    [InlineData("acme .atlassian.net")]
    [InlineData("acme.atlassian.net\n.evil.com")]
    [InlineData("acme.atl\u0430ssian.net")]
    [InlineData("ftp://acme.atlassian.net")]
    [InlineData("javascript://acme.atlassian.net")]
    [InlineData("acme.atlassian.net:")]
    [InlineData("acme.atlassian.net:80:443")]
    [InlineData("acme.atlassian.net:https")]
    [InlineData("127.0.0.1")]
    [InlineData("https://127.0.0.1/")]
    [InlineData("2130706433")]
    [InlineData("0x7f000001")]
    [InlineData("0177.0.0.1")]
    [InlineData("0x7f.1")]
    [InlineData("10.0.0.1:443")]
    [InlineData("[::1]")]
    [InlineData("https://[::ffff:127.0.0.1]:443/")]
    [InlineData("169.254.169.254")]
    public void Refuses(string? input) {
        Assert.False(JiraHostPolicy.AtlassianCloudOnly.TryNormalize(input, out var host));
        Assert.Null(host);
    }

    [Fact]
    public void Allowlisted_hosts_pass_exactly() {
        var policy = JiraHostPolicy.FromAllowlist(" jira.example.com , JIRA.corp.example.org ");

        Assert.True(policy.TryNormalize("https://jira.example.com/", out var host));
        Assert.Equal("jira.example.com", host);
        Assert.True(policy.TryNormalize("jira.corp.example.org", out _));
        Assert.True(policy.TryNormalize("acme.atlassian.net", out _));
        Assert.False(policy.TryNormalize("evil.jira.example.com", out _));
        Assert.False(policy.TryNormalize("jira.example.com.evil.com", out _));
        Assert.False(policy.TryNormalize("example.com", out _));
    }

    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.1")]
    [InlineData("0x7f.1")]
    [InlineData("2130706433")]
    [InlineData("localhost")]
    [InlineData("user@jira.example.com")]
    [InlineData("-bad.example.com")]
    public void Allowlist_entries_must_be_dns_names(string entry) {
        Assert.Throws<ArgumentException>(() => JiraHostPolicy.FromAllowlist(entry));
    }

    [Fact]
    public void Empty_allowlist_means_atlassian_cloud_only() {
        var policy = JiraHostPolicy.FromAllowlist("");

        Assert.True(policy.TryNormalize("acme.atlassian.net", out _));
        Assert.False(policy.TryNormalize("jira.example.com", out _));
    }
}
