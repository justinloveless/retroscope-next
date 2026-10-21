using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Retroscope.Server.Auth.Email;

namespace Retroscope.Integration.Tests.Email;

[Trait("Category", "Email")]
public sealed class SmtpSenderTests {
    private static readonly OutboxMail Mail = new("ada@example.com", "Hello", "Line one\n.leading dot\nhttps://retro.test/auth/magic#t=abc");

    [Fact]
    public async Task Starttls_upgrades_before_authenticating_and_sending() {
        await using var smtp = new FakeSmtpServer(FakeSmtpMode.StartTls);

        await Sender(smtp, SmtpSecurity.StartTls).SendAsync(Mail, TestContext.Current.CancellationToken);

        var mail = Assert.Single(smtp.Received);
        Assert.True(mail.Tls);
        Assert.Equal("mailer", mail.AuthUser);
        var commands = smtp.Commands.ToList();
        Assert.Equal("EHLO retro.test", commands[0]);
        Assert.Equal("STARTTLS", commands[1]);
        Assert.All(commands.Skip(2), x => Assert.StartsWith("tls ", x, StringComparison.Ordinal));
        Assert.Contains(commands, x => x.StartsWith("tls AUTH PLAIN ", StringComparison.Ordinal));
        Assert.Equal("Line one\r\n.leading dot\r\nhttps://retro.test/auth/magic#t=abc\r\n", mail.Body);
        Assert.Equal("7bit", mail.Header("Content-Transfer-Encoding"));
        Assert.Equal("ada@example.com", mail.Header("To"));
        Assert.Matches(@"^<[0-9a-f]{32}@retro\.test>$", mail.Header("Message-ID"));
    }

    [Fact]
    public async Task Implicit_tls_is_encrypted_from_the_first_byte() {
        await using var smtp = new FakeSmtpServer(FakeSmtpMode.ImplicitTls);

        await Sender(smtp, SmtpSecurity.ImplicitTls).SendAsync(Mail, TestContext.Current.CancellationToken);

        Assert.True(Assert.Single(smtp.Received).Tls);
        Assert.Equal("tls EHLO retro.test", smtp.Commands.First());
    }

    [Fact]
    public async Task A_server_that_does_not_offer_starttls_gets_no_credentials_and_no_mail() {
        await using var smtp = new FakeSmtpServer(FakeSmtpMode.StartTls) { AdvertiseStartTls = false };

        var error = await Assert.ThrowsAsync<SmtpFailureException>(
            () => Sender(smtp, SmtpSecurity.StartTls).SendAsync(Mail, TestContext.Current.CancellationToken));

        Assert.Contains("STARTTLS", error.Message, StringComparison.Ordinal);
        Assert.Equal(["EHLO retro.test"], smtp.Commands);
        Assert.Empty(smtp.Received);
    }

    [Fact]
    public async Task An_untrusted_certificate_is_refused_by_default() {
        await using var smtp = new FakeSmtpServer(FakeSmtpMode.StartTls);
        var options = Options(smtp, SmtpSecurity.StartTls) with { CertificateValidation = null };

        await Assert.ThrowsAsync<SmtpFailureException>(
            () => new SmtpSender(options, TimeProvider.System).SendAsync(Mail, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(smtp.Commands, x => x.Contains("AUTH", StringComparison.Ordinal));
        Assert.Empty(smtp.Received);
    }

    [Fact]
    public async Task Plaintext_injected_after_the_starttls_reply_fails_the_send() {
        await using var smtp = new FakeSmtpServer(FakeSmtpMode.StartTls) { InjectAfterStartTls = true };

        var error = await Assert.ThrowsAsync<SmtpFailureException>(
            () => Sender(smtp, SmtpSecurity.StartTls).SendAsync(Mail, TestContext.Current.CancellationToken));

        Assert.Contains("after its STARTTLS reply", error.Message, StringComparison.Ordinal);
        Assert.Empty(smtp.Received);
    }

    [Fact]
    public async Task Auth_login_is_used_when_plain_is_not_offered() {
        await using var smtp = new FakeSmtpServer(FakeSmtpMode.StartTls) { AuthMechanisms = "LOGIN" };

        await Sender(smtp, SmtpSecurity.StartTls).SendAsync(Mail, TestContext.Current.CancellationToken);

        Assert.Equal("mailer", Assert.Single(smtp.Received).AuthUser);
        Assert.Contains("tls AUTH LOGIN", smtp.Commands);
    }

    [Fact]
    public async Task Wrong_credentials_fail_with_the_reply_code() {
        await using var smtp = new FakeSmtpServer(FakeSmtpMode.StartTls) { Credentials = ("mailer", "other") };

        var error = await Assert.ThrowsAsync<SmtpFailureException>(
            () => Sender(smtp, SmtpSecurity.StartTls).SendAsync(Mail, TestContext.Current.CancellationToken));

        Assert.Equal("SMTP AUTH answered 535.", error.Message);
    }

    [Fact]
    public async Task Credentials_are_never_sent_without_tls() {
        await using var smtp = new FakeSmtpServer(FakeSmtpMode.Plain);

        await Assert.ThrowsAsync<SmtpFailureException>(
            () => Sender(smtp, SmtpSecurity.None).SendAsync(Mail, TestContext.Current.CancellationToken));

        Assert.DoesNotContain(smtp.Commands, x => x.StartsWith("AUTH", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_relay_without_tls_or_auth_works_when_configured_so() {
        await using var smtp = new FakeSmtpServer(FakeSmtpMode.Plain) { Credentials = null };
        var options = Options(smtp, SmtpSecurity.None) with { User = null, Password = null };

        await new SmtpSender(options, TimeProvider.System).SendAsync(Mail, TestContext.Current.CancellationToken);

        Assert.False(Assert.Single(smtp.Received).Tls);
    }

    [Fact]
    public async Task Non_ascii_bodies_and_subjects_are_encoded() {
        await using var smtp = new FakeSmtpServer(FakeSmtpMode.StartTls);
        var mail = new OutboxMail("ada@example.com", "Équipe", "Ça marche: \"Équipe\"");

        await Sender(smtp, SmtpSecurity.StartTls).SendAsync(mail, TestContext.Current.CancellationToken);

        var received = Assert.Single(smtp.Received);
        Assert.Equal("base64", received.Header("Content-Transfer-Encoding"));
        Assert.Equal("Ça marche: \"Équipe\"", Encoding.UTF8.GetString(Convert.FromBase64String(received.Body.Replace("\r\n", "", StringComparison.Ordinal))));
        Assert.Equal("=?utf-8?B?w4lxdWlwZQ==?=", received.Header("Subject"));
    }

    [Fact]
    public async Task A_non_ascii_address_needs_smtputf8() {
        await using var smtp = new FakeSmtpServer(FakeSmtpMode.StartTls);
        var mail = new OutboxMail("zoë@example.com", "Hello", "Hi");

        await Assert.ThrowsAsync<SmtpFailureException>(
            () => Sender(smtp, SmtpSecurity.StartTls).SendAsync(mail, TestContext.Current.CancellationToken));
        smtp.AdvertiseSmtpUtf8 = true;
        await Sender(smtp, SmtpSecurity.StartTls).SendAsync(mail, TestContext.Current.CancellationToken);

        Assert.Equal(["<zoë@example.com>"], Assert.Single(smtp.Received).Recipients);
        Assert.Contains("tls MAIL FROM:<no-reply@retro.test> SMTPUTF8", smtp.Commands);
    }

    [Fact]
    public async Task A_silent_server_times_out() {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var options = new SmtpOptions {
            Host = "127.0.0.1",
            Port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port,
            Security = SmtpSecurity.None,
            FromAddress = "no-reply@retro.test",
            Timeout = TimeSpan.FromMilliseconds(300),
        };

        var error = await Assert.ThrowsAsync<SmtpFailureException>(
            () => new SmtpSender(options, TimeProvider.System).SendAsync(Mail, TestContext.Current.CancellationToken));

        Assert.Equal("The SMTP server did not answer in time.", error.Message);
    }

    [Theory]
    [InlineData("587", null, SmtpSecurity.StartTls)]
    [InlineData("465", null, SmtpSecurity.ImplicitTls)]
    [InlineData("2525", "tls", SmtpSecurity.ImplicitTls)]
    [InlineData("1025", "none", SmtpSecurity.None)]
    public void Security_follows_the_port_unless_set(string port, string? security, SmtpSecurity expected) {
        var options = SmtpOptions.FromConfiguration(Configuration(new() {
            ["SMTP_HOST"] = "smtp.example.com",
            ["SMTP_PORT"] = port,
            ["SMTP_SECURITY"] = security,
            ["SMTP_FROM"] = "Retroscope <no-reply@example.com>",
            ["PUBLIC_URL"] = "https://retro.example.com",
        }));

        Assert.Equal(expected, options.Security);
        Assert.Equal("Retroscope", options.FromName);
        Assert.Equal("no-reply@example.com", options.FromAddress);
        Assert.Equal("retro.example.com", options.HeloName);
    }

    [Theory]
    [InlineData("Retroscope <no-reply@example.com>\r\nBcc: x@example.com")]
    [InlineData("not an address")]
    [InlineData("")]
    public void A_bad_from_address_fails_at_startup(string from) =>
        Assert.Throws<InvalidOperationException>(() => SmtpOptions.FromConfiguration(Configuration(new() {
            ["SMTP_HOST"] = "smtp.example.com",
            ["SMTP_FROM"] = from,
        })));

    [Fact]
    public void Without_a_host_nothing_is_configured() =>
        Assert.False(SmtpOptions.FromConfiguration(Configuration([])).IsConfigured);

    private static SmtpSender Sender(FakeSmtpServer smtp, SmtpSecurity security) =>
        new(Options(smtp, security), new FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 9, 30, 0, TimeSpan.Zero)));

    private static SmtpOptions Options(FakeSmtpServer smtp, SmtpSecurity security) => new() {
        Host = "127.0.0.1",
        Port = smtp.Port,
        Security = security,
        User = "mailer",
        Password = "s3cret",
        FromAddress = "no-reply@retro.test",
        FromName = "Retroscope",
        HeloName = "retro.test",
        CertificateValidation = smtp.Validate,
    };

    private static IConfiguration Configuration(Dictionary<string, string?> values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values).Build();
}
