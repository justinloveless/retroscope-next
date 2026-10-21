using System.Text;
using Microsoft.Extensions.Time.Testing;
using Retroscope.Core.Ids;
using Retroscope.Core.Security;
using Retroscope.Server.Data;
using Retroscope.Server.Jira;

namespace Retroscope.Integration.Tests.Jira.Client;

[Trait("Category", "JiraClient")]
public sealed class CredentialsTests : IAsyncDisposable {
    // Never opened: these tests only seal and open boxes.
    private readonly Npgsql.NpgsqlDataSource _dataSource = Db.CreateDataSource("Host=unused;Username=retroscope_app;Password=x");
    private readonly Keyring _keyring = Keyring.Parse(JiraHarness.NewKeys("k7"));

    public ValueTask DisposeAsync() => _dataSource.DisposeAsync();

    [Fact]
    public void A_sealed_box_opens_to_the_Basic_parameter_and_names_its_key() {
        var credentials = new Credentials(_keyring, _dataSource, new FakeTimeProvider());
        var team = new TeamId(Guid.NewGuid());

        var box = credentials.Seal(team, "ada@acme.com", "ATATT-secret-9f3c");

        Assert.Equal("k7", box.KeyId);
        Assert.Equal("9f3c", box.TokenLast4);
        Assert.Equal("a•••@acme.com", box.EmailMasked);
        Assert.DoesNotContain("ATATT", Encoding.Latin1.GetString(box.Box), StringComparison.Ordinal);
        var parameter = credentials.TryCreateBasicParameter(team, box.Box);
        Assert.Equal("ada@acme.com:ATATT-secret-9f3c", Encoding.UTF8.GetString(Convert.FromBase64String(parameter!)));
    }

    [Fact]
    public void A_box_is_bound_to_its_team() {
        var credentials = new Credentials(_keyring, _dataSource, new FakeTimeProvider());
        var box = credentials.Seal(new TeamId(Guid.NewGuid()), "ada@acme.com", "token-1234");

        Assert.Null(credentials.TryCreateBasicParameter(new TeamId(Guid.NewGuid()), box.Box));
    }

    [Fact]
    public void A_tampered_box_does_not_open() {
        var credentials = new Credentials(_keyring, _dataSource, new FakeTimeProvider());
        var team = new TeamId(Guid.NewGuid());
        var box = credentials.Seal(team, "ada@acme.com", "token-1234").Box;
        box[^1] ^= 1;

        Assert.Null(credentials.TryCreateBasicParameter(team, box));
        Assert.Null(credentials.TryCreateBasicParameter(team, []));
    }

    [Fact]
    public void Escaped_characters_survive_the_round_trip() {
        var credentials = new Credentials(_keyring, _dataSource, new FakeTimeProvider());
        var team = new TeamId(Guid.NewGuid());
        var box = credentials.Seal(team, "zoë+\"q\"@acme.com", "tok\\en:with-colon");

        var parameter = credentials.TryCreateBasicParameter(team, box.Box);

        Assert.Equal("zoë+\"q\"@acme.com:tok\\en:with-colon", Encoding.UTF8.GetString(Convert.FromBase64String(parameter!)));
    }

    [Fact]
    public void The_associated_data_is_the_prefix_and_the_team_bytes_in_RFC_4122_order() {
        var team = new TeamId(Guid.Parse("00112233-4455-6677-8899-aabbccddeeff"));

        Assert.Equal("6a6972613a76313a00112233445566778899aabbccddeeff", Convert.ToHexStringLower(Credentials.AssociatedData(team)));
    }

    [Theory]
    [InlineData("ada@acme.com", "a•••@acme.com")]
    [InlineData("x@y", "x•••@y")]
    [InlineData("no-at-sign", "•••")]
    public void Emails_are_masked_for_display(string email, string masked) => Assert.Equal(masked, Credentials.MaskEmail(email));
}
