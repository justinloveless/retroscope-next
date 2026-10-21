using Retroscope.Server.Auth.Email;

namespace Retroscope.Integration.Tests.Email;

[Trait("Category", "Email")]
public sealed class TemplatesTests {
    [Theory]
    [InlineData("Platform Team", "Platform Team")]
    [InlineData("Win a prize at https://evil.example", "Win a prize at")]
    [InlineData("Visit evil.com now", "Visit now")]
    [InlineData("www.evil.example team", "team")]
    [InlineData("Line one\r\nBcc: x", "Line one Bcc: x")]
    [InlineData("Mail me@evil.example", "Mail")]
    [InlineData("say \"hi\"", "say hi")]
    [InlineData("Team v2.0", "Team v2.0")]
    public void Invite_team_names_are_one_inert_line(string name, string expected) =>
        Assert.Equal(expected, Templates.InertTeamName(name));

    [Fact]
    public void Every_template_is_plain_text_with_a_header_safe_subject() {
        var link = "https://retro.test/auth/magic#t=abc";
        MailContent[] all = [
            Templates.VerifyEmail(link),
            Templates.AlreadyRegistered("https://retro.test"),
            Templates.MagicLink(link),
            Templates.LoginCapped(link),
            Templates.PasswordReset(link),
            Templates.TeamInvite("Ops", link),
        ];

        Assert.All(all, x => {
            Assert.True(MailHeaders.IsSafe(x.Subject));
            Assert.DoesNotContain('<', x.Body);
        });
    }
}
