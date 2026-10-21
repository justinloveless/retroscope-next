using Retroscope.Core.Ids;
using Retroscope.Importer.Map;

namespace Retroscope.Importer.Tests.Infrastructure;

[Trait("Category", "Source")]
public sealed class CommonRulesTests {
    private static readonly DateTime T0 = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Duplicate_emails_merge_into_the_latest_sign_in() {
        Guid a = Id(1), b = Id(2), c = Id(3);

        var directory = PrincipalDirectory.Build([
            new AuthAccount(a, "Dup@Example.com ", false, T0, T0),
            new AuthAccount(b, "dup@example.com", false, T0.AddDays(1), T0.AddDays(1)),
            new AuthAccount(c, "dup@example.com", false, null, T0.AddDays(-1)),
        ]);

        Assert.Equal(b, directory.User(a));
        Assert.Equal(b, directory.User(b));
        Assert.Equal(b, directory.User(c));
        Assert.Equal(new Dictionary<Guid, Guid> { [a] = b, [c] = b }, directory.Merged);
        Assert.Equal("dup@example.com", directory.Emails[b]);
        Assert.True(directory.IsKeptUser(b));
        Assert.False(directory.IsKeptUser(a));
    }

    [Fact]
    public void Ties_break_on_the_earliest_creation_then_the_smallest_id() {
        Guid a = Id(1), b = Id(2), c = Id(3);

        var byCreation = PrincipalDirectory.Build([
            new AuthAccount(a, "x@example.com", false, T0, T0.AddDays(1)),
            new AuthAccount(b, "x@example.com", false, T0, T0),
        ]);
        var byId = PrincipalDirectory.Build([
            new AuthAccount(c, "y@example.com", false, null, null),
            new AuthAccount(b, "y@example.com", false, null, null),
        ]);

        Assert.Equal(b, byCreation.User(a));
        Assert.Equal(b, byId.User(c));
    }

    [Fact]
    public void Anonymous_accounts_are_guests_and_phone_only_accounts_are_skipped() {
        Guid user = Id(1), guest = Id(2), phone = Id(3), unknown = Id(4);

        var directory = PrincipalDirectory.Build([
            new AuthAccount(user, "u@example.com", false, null, null),
            new AuthAccount(guest, null, true, null, null),
            new AuthAccount(phone, "  ", false, null, null),
        ]);

        Assert.Equal([phone], directory.SkippedPhoneOnly);
        Assert.Null(directory.User(guest));
        Assert.Equal(guest, directory.Principal(guest));
        Assert.Null(directory.Principal(phone));
        Assert.Null(directory.Principal(unknown));
        Assert.Null(directory.User(null));
    }

    [Theory]
    [InlineData("Sneaky Penguin", "Sneaky Penguin")]
    [InlineData("  Ana  ", "Ana")]
    [InlineData("", "Guest")]
    [InlineData(null, "Guest")]
    [InlineData("bob@example.com", "Guest-")]
    [InlineData("Bob (bob.smith@corp.example.org)", "Guest-")]
    [InlineData("@channel", "@channel")]
    [InlineData("a@b", "a@b")]
    public void Display_names_are_scrubbed(string? name, string expected) {
        var scrubbed = DisplayNames.Scrub(name);

        if (expected == "Guest-") {
            Assert.Matches("^Guest-[0-9a-f]{4}$", scrubbed);
            Assert.Equal(scrubbed, DisplayNames.Scrub(name!.ToUpperInvariant()));
        } else {
            Assert.Equal(expected, scrubbed);
        }
    }

    [Fact]
    public void Long_display_names_are_capped() =>
        Assert.Equal(new string('x', DisplayNames.MaxLength), DisplayNames.Scrub(new string('x', 80)));

    [Fact]
    public void Synthetic_ids_are_name_based_and_stable() {
        Assert.Equal(UuidV5.Create(SyntheticIds.GuestNamespace, "guest:abc"), SyntheticIds.Guest("abc"));
        Assert.Equal(SyntheticIds.Guest("abc"), SyntheticIds.Guest("abc"));
        Assert.NotEqual(SyntheticIds.Guest("abc"), SyntheticIds.Slack("abc"));
        Assert.Equal(5, SyntheticIds.Slack("U123").Version);
        Assert.Equal(new Guid("6d955143-df87-5e3d-9f5a-e71d5d85cd58"), SyntheticIds.Guest("session-1"));
    }

    [Fact]
    public void Anonymous_content_records_no_creator() {
        Assert.Null(Anonymity.CreatedBy(anonymousSource: true, Id(1)));
        Assert.Equal(Id(1), Anonymity.CreatedBy(anonymousSource: false, Id(1)));
    }

    private static Guid Id(int n) => new($"00000000-0000-0000-0000-{n:D12}");
}
