using System.Net;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Auth.Email;
using Retroscope.Server.Hosting;
using Retroscope.Server.Jobs;

namespace Retroscope.Integration.Tests.Email;

[Trait("Category", "Email")]
[Collection(SharedPostgres.Name)]
public sealed class OutboxTests(PostgresFixture postgres) {
    [Theory]
    [InlineData("/api/auth/magic/request")]
    [InlineData("/api/auth/reset/request")]
    public async Task Known_and_unknown_addresses_get_identical_responses(string path) {
        await using var host = await EmailHost.StartAsync(postgres);
        await host.CreateUserAsync("ada@example.com", verified: true);

        using var known = await host.Browser().PostAsync(path, new { email = "ada@example.com" });
        using var unknown = await host.Browser().PostAsync(path, new { email = "nobody@example.com" });

        Assert.Equal(HttpStatusCode.Accepted, known.StatusCode);
        Assert.Equal(known.StatusCode, unknown.StatusCode);
        Assert.Equal(Describe(known), Describe(unknown));
        Assert.Equal(await known.Content.ReadAsStringAsync(EmailHost.Token), await unknown.Content.ReadAsStringAsync(EmailHost.Token));
    }

    [Fact]
    public async Task Over_the_email_budget_requests_still_answer_202_and_the_mail_is_dropped() {
        await using var host = await EmailHost.StartAsync(postgres);
        await host.CreateUserAsync("ada@example.com", verified: true);
        var budget = host.Service<EmailBudget>();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 2; i++) {
            using var magic = await host.Browser().PostAsync("/api/auth/magic/request", new { email = "ada@example.com" });
            using var reset = await host.Browser().PostAsync("/api/auth/reset/request", new { email = "ada@example.com" });
            statuses.Add(magic.StatusCode);
            statuses.Add(reset.StatusCode);
        }

        Assert.All(statuses, x => Assert.Equal(HttpStatusCode.Accepted, x));
        Assert.Equal(3L, await host.ScalarAsync<long>("select count(*) from email_outbox"));
        Assert.Equal(3L, await host.ScalarAsync<long>("select count(*) from auth_tokens"));
        Assert.Equal(1L, budget.Dropped);
    }

    [Fact]
    public async Task More_than_three_magic_requests_an_hour_for_one_address_send_nothing_more() {
        await using var host = await EmailHost.StartAsync(postgres);
        var limiter = host.Service<RateLimiter>();

        for (var i = 0; i < 5; i++) {
            using var response = await host.Browser().PostAsync("/api/auth/magic/request", new { email = $"user{i % 2}@example.com" });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }
        for (var i = 0; i < 2; i++) {
            using var response = await host.Browser().PostAsync("/api/auth/magic/request", new { email = "user0@example.com" });
            Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        }

        Assert.Equal(3L, await host.ScalarAsync<long>("select count(*) from auth_tokens where email = 'user0@example.com'"));
        Assert.Equal(0L, host.Service<EmailBudget>().Dropped);
        Assert.False(limiter.TryAcquire(EmailEndpoints.MagicPerEmail, new RateKeys(null, null, "user0@example.com"), out _));
    }

    [Fact]
    public async Task A_failing_mail_backs_off_and_after_the_sixth_failure_its_body_is_nulled() {
        await using var host = await EmailHost.StartAsync(postgres);
        host.Smtp.RcptReply = 451;
        await host.Browser().PostAsync("/api/auth/magic/request", new { email = "ada@example.com" });

        for (var attempt = 1; attempt <= EmailWorker.MaxAttempts; attempt++) {
            Assert.Equal(1, await host.DeliverAsync());
            Assert.Equal(0, await host.DeliverAsync());
            Assert.Equal(attempt, await host.ScalarAsync<int>("select attempts from email_outbox"));
            Assert.Equal("SMTP RCPT answered 451.", await host.ScalarAsync<string>("select last_error from email_outbox"));
            if (attempt < EmailWorker.MaxAttempts) {
                Assert.NotNull(await host.ScalarAsync<string>("select body_text from email_outbox"));
                var wait = EmailWorker.Backoff[attempt - 1];
                host.Time.Advance(wait - TimeSpan.FromSeconds(1));
                Assert.Equal(0, await host.DeliverAsync());
                host.Time.Advance(TimeSpan.FromSeconds(1));
            }
        }

        Assert.True(await host.ScalarAsync<bool>("select body_text is null from email_outbox"));
        host.Time.Advance(TimeSpan.FromDays(1));
        Assert.Equal(0, await host.DeliverAsync());
        Assert.Empty(host.Smtp.Received);
    }

    [Fact]
    public async Task A_mail_that_failed_once_is_delivered_and_deleted_on_retry() {
        await using var host = await EmailHost.StartAsync(postgres);
        host.Smtp.RcptReply = 451;
        await host.Browser().PostAsync("/api/auth/magic/request", new { email = "ada@example.com" });
        await host.DeliverAsync();

        host.Smtp.RcptReply = null;
        host.Time.Advance(EmailWorker.Backoff[0]);
        await host.DeliverAsync();

        Assert.Single(host.Smtp.Received);
        Assert.Equal(0L, await host.ScalarAsync<long>("select count(*) from email_outbox"));
    }

    [Fact]
    public async Task The_background_worker_sends_as_soon_as_a_mail_is_queued() {
        await using var host = await EmailHost.StartAsync(postgres, runWorker: true);

        await host.Browser().PostAsync("/api/auth/magic/request", new { email = "ada@example.com" });
        var mail = await host.Smtp.WaitForMailAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["<ada@example.com>"], mail.Recipients);
    }

    [Theory]
    [InlineData("ada@example.com\r\nBcc: victim@example.com", "Subject")]
    [InlineData("ada@example.com", "Hello\r\nBcc: victim@example.com")]
    [InlineData("ada@example.com\n", "Hello")]
    [InlineData("ada@example.com", "Hello\rX-Injected: 1")]
    [InlineData("ada@example.com, eve@example.com", "Hello")]
    [InlineData("<ada@example.com>", "Hello")]
    public void Values_that_reach_a_header_are_rejected_when_they_hold_line_breaks(string to, string subject) =>
        Assert.Throws<ArgumentException>(() => new OutboxMail(to, subject, "body"));

    [Fact]
    public async Task The_database_refuses_a_subject_with_a_line_break_written_around_the_outbox() {
        await using var host = await EmailHost.StartAsync(postgres);

        var error = await Assert.ThrowsAsync<Npgsql.PostgresException>(() => host.ExecuteAsync(
            "insert into email_outbox (to_address, subject, body_text) values ('ada@example.com', $1, 'x')", "Hi\r\nBcc: x@example.com"));

        Assert.Equal(Npgsql.PostgresErrorCodes.CheckViolation, error.SqlState);
    }

    [Fact]
    public async Task A_row_with_an_injected_recipient_is_never_sent() {
        await using var host = await EmailHost.StartAsync(postgres);
        await host.ExecuteAsync(
            "insert into email_outbox (to_address, subject, body_text, next_attempt_at) values ($1, 'Hello', 'x', $2)",
            "ada@example.com>\r\nRCPT TO:<victim@example.com",
            host.Time.GetUtcNow());

        await host.DeliverAsync();

        Assert.Empty(host.Smtp.Received);
        Assert.Equal(0, host.Smtp.Connections);
        Assert.Equal(1, await host.ScalarAsync<int>("select attempts from email_outbox"));
    }

    private static string Describe(HttpResponseMessage response) =>
        string.Join(
            "\n",
            response.Headers.Concat(response.Content.Headers)
                .Where(x => x.Key is not "Date")
                .OrderBy(x => x.Key, StringComparer.Ordinal)
                .Select(x => $"{x.Key}: {string.Join(",", x.Value)}"));
}
