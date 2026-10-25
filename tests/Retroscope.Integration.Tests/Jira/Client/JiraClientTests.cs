using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Retroscope.Core.Ids;
using Retroscope.Core.Security;
using Retroscope.Fakes.FakeJira;
using Retroscope.Integration.Tests.Schema;
using Retroscope.Server.Jira;

namespace Retroscope.Integration.Tests.Jira.Client;

[Trait("Category", "JiraClient")]
[Collection(SharedPostgres.Name)]
public sealed class JiraClientTests(PostgresFixture postgres) {
    private const string BoardField = "customfield_10028";

    private static readonly string[] DetailFields = ["summary", "status", "customfield_10016", "customfield_10020"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Reads_an_issue_with_basic_auth_through_the_vetted_address() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.AddSprint(5, "Sprint 5", "active", "2026-10-01T09:00:00.000Z");
        h.Jira.AddIssue("DUN-1", "First", sprintId: 5, points: 2.5m);

        var issue = await h.Client.GetIssueAsync(h.Team, "DUN-1", DetailFields, Ct);

        Assert.Equal("First", issue.Fields!.Summary);
        Assert.Equal(2.5m, JiraMap.ReadPoints(issue, "customfield_10016"));
        Assert.Equal("Sprint 5", JiraMap.ReadSprint(issue, "customfield_10020")!.Name);
        var request = Assert.Single(h.Jira.Requests);
        Assert.Equal(JiraHarness.SiteHost, request.Host);
        Assert.Equal(h.Jira.BasicAuthorization, request.Authorization);
        Assert.Equal("/rest/api/3/issue/DUN-1", request.Path);
        Assert.Contains(JiraHarness.SiteHost, h.Resolver.Lookups);
    }

    [Fact]
    public async Task Waits_for_Retry_After_on_429_then_succeeds() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.Script("GET", "/rest/api/3/myself", new FakeJiraResponse(429, "{}", new Dictionary<string, string> { ["Retry-After"] = "3" }));

        var myself = await h.Client.GetMyselfAsync(h.Team, Ct);

        Assert.Equal("acc-bot", myself.AccountId);
        Assert.Equal(2, h.Jira.Requests.Count);
        Assert.Equal([TimeSpan.FromSeconds(3)], h.Delays);
    }

    [Fact]
    public async Task Retry_After_as_an_HTTP_date_is_honoured() {
        await using var h = await JiraHarness.StartAsync(postgres);
        var at = h.Time.GetUtcNow().AddSeconds(4).ToString("R");
        h.Jira.Script("GET", "/rest/api/3/myself", new FakeJiraResponse(503, "{}", new Dictionary<string, string> { ["Retry-After"] = at }));

        await h.Client.GetMyselfAsync(h.Team, Ct);

        Assert.Equal([TimeSpan.FromSeconds(4)], h.Delays);
    }

    [Fact]
    public async Task Retry_After_beyond_the_cap_is_not_waited_out_and_reports_rate_limited() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.Script("GET", "/rest/api/3/myself", new FakeJiraResponse(429, "{}", new Dictionary<string, string> { ["Retry-After"] = "60" }));

        var error = await Assert.ThrowsAsync<JiraException>(() => h.Client.GetMyselfAsync(h.Team, Ct));

        Assert.Equal(JiraErrorCode.RateLimited, error.Error.Code);
        Assert.Equal(503, error.Error.HttpStatus);
        Assert.Equal(TimeSpan.FromSeconds(60), error.Error.RetryAfter);
        Assert.Single(h.Jira.Requests);
        Assert.Empty(h.Delays);
    }

    [Fact]
    public async Task Gives_up_after_two_retries() {
        await using var h = await JiraHarness.StartAsync(postgres);
        var tooMany = new FakeJiraResponse(429, "{}", new Dictionary<string, string> { ["Retry-After"] = "1" });
        h.Jira.Script("GET", "/rest/api/3/myself", tooMany, tooMany, tooMany, tooMany);

        var error = await Assert.ThrowsAsync<JiraException>(() => h.Client.GetMyselfAsync(h.Team, Ct));

        Assert.Equal(JiraErrorCode.RateLimited, error.Error.Code);
        Assert.Equal(TimeSpan.FromSeconds(1), error.Error.RetryAfter);
        Assert.Equal(3, h.Jira.Requests.Count);
    }

    [Fact]
    public async Task Backs_off_with_full_jitter_when_503_has_no_Retry_After() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.Script("GET", "/rest/api/3/myself", new FakeJiraResponse(503), new FakeJiraResponse(503));

        await h.Client.GetMyselfAsync(h.Team, Ct);

        // Jitter is pinned at 0.5: half of 250 ms, then half of 500 ms.
        Assert.Equal([TimeSpan.FromMilliseconds(125), TimeSpan.FromMilliseconds(250)], h.Delays);
        Assert.Equal(3, h.Jira.Requests.Count);
    }

    [Fact]
    public async Task A_post_is_never_retried_after_a_response() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.Script("POST", "/rest/api/3/issue", new FakeJiraResponse(503, "{}", new Dictionary<string, string> { ["Retry-After"] = "1" }));

        var error = await Assert.ThrowsAsync<JiraException>(
            () => h.Client.CreateIssueAsync(h.Team, new JiraIssueCreate("DUN", "New"), Ct));

        Assert.Equal(JiraErrorCode.Unavailable, error.Error.Code);
        Assert.Single(h.Jira.Requests);
    }

    [Fact]
    public async Task A_post_is_retried_after_a_connect_failure() {
        // ::1 refuses (the fake listens on 127.0.0.1 only), so the first connect fails before anything is sent.
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Resolver.Answer(JiraHarness.SiteHost, [IPAddress.IPv6Loopback], [IPAddress.Loopback]);

        var created = await h.Client.CreateIssueAsync(h.Team, new JiraIssueCreate("DUN", "New"), Ct);

        Assert.Equal("DUN-1", created.Key);
        Assert.Single(h.Jira.Requests);
        Assert.Single(h.Delays);
    }

    [Fact]
    public async Task A_put_is_retried_after_503() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.AddIssue("DUN-1", "First");
        h.Jira.Script("PUT", "/rest/api/3/issue/DUN-1", new FakeJiraResponse(503));

        await h.Client.UpdateIssueAsync(h.Team, "DUN-1", new JiraIssueUpdate(Summary: "Renamed"), Ct);

        Assert.Equal("Renamed", (string?)h.Jira.Issues["DUN-1"]["fields"]!["summary"]);
        Assert.Equal(2, h.Jira.Requests.Count);
    }

    [Fact]
    public async Task Five_consecutive_5xx_open_the_breaker_for_30_seconds() {
        await using var h = await JiraHarness.StartAsync(postgres);
        var failure = new FakeJiraResponse(500, "{}");
        h.Jira.Script("GET", "/rest/api/3/myself", failure, failure, failure, failure, failure, failure);

        for (var i = 0; i < 5; i++) {
            var error = await Assert.ThrowsAsync<JiraException>(() => h.Client.GetMyselfAsync(h.Team, Ct));
            Assert.Equal(JiraErrorCode.Unavailable, error.Error.Code);
        }

        var open = await Assert.ThrowsAsync<JiraException>(() => h.Client.GetMyselfAsync(h.Team, Ct));
        Assert.Equal(JiraErrorCode.Unavailable, open.Error.Code);
        Assert.Equal(5, h.Jira.Requests.Count);
        Assert.True(h.Throttle.IsOpen(h.Team));

        h.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.False(h.Throttle.IsOpen(h.Team));
        await Assert.ThrowsAsync<JiraException>(() => h.Client.GetMyselfAsync(h.Team, Ct));
        Assert.True(h.Throttle.IsOpen(h.Team));

        h.Time.Advance(TimeSpan.FromSeconds(30));
        Assert.Equal("acc-bot", (await h.Client.GetMyselfAsync(h.Team, Ct)).AccountId);
        Assert.Equal(7, h.Jira.Requests.Count);
    }

    [Fact]
    public async Task The_breaker_is_per_team() {
        await using var h = await JiraHarness.StartAsync(postgres);
        var other = new TeamId(Guid.NewGuid());
        await h.ConfigureTeamAsync(other, h.Jira.Email, h.Jira.Token);
        for (var i = 0; i < 5; i++) {
            h.Throttle.RecordFailure(h.Team);
        }

        await Assert.ThrowsAsync<JiraException>(() => h.Client.GetMyselfAsync(h.Team, Ct));
        Assert.Equal("acc-bot", (await h.Client.GetMyselfAsync(other, Ct)).AccountId);
    }

    [Fact]
    public async Task A_timed_out_attempt_maps_to_unavailable_and_counts_toward_the_breaker() {
        await using var h = await JiraHarness.StartAsync(postgres);
        var gate = new TaskCompletionSource();
        h.Jira.Gate = gate.Task;
        for (var i = 0; i < 4; i++) {
            h.Throttle.RecordFailure(h.Team);
        }

        var call = h.Client.GetMyselfAsync(h.Team, Ct);
        await h.WaitForRequestsAsync(1);
        h.Time.Advance(TimeSpan.FromSeconds(10));

        var error = await Assert.ThrowsAsync<JiraException>(() => call);
        Assert.Equal(JiraErrorCode.Unavailable, error.Error.Code);
        Assert.True(h.Throttle.IsOpen(h.Team));
        gate.SetResult();
    }

    [Fact]
    public async Task Identical_concurrent_reads_share_one_request() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.AddIssue("DUN-1", "First");
        var gate = new TaskCompletionSource();
        h.Jira.Gate = gate.Task;

        var calls = Enumerable.Range(0, 10).Select(_ => h.Client.GetIssueAsync(h.Team, "DUN-1", DetailFields, Ct)).ToList();
        await h.WaitForRequestsAsync(1);
        gate.SetResult();
        var issues = await Task.WhenAll(calls);

        Assert.All(issues, i => Assert.Equal("First", i.Fields!.Summary));
        Assert.Single(h.Jira.Requests);
    }

    [Fact]
    public async Task One_caller_giving_up_does_not_fail_the_others_sharing_its_request() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.AddIssue("DUN-1", "First");
        var gate = new TaskCompletionSource();
        h.Jira.Gate = gate.Task;
        using var impatient = new CancellationTokenSource();

        var cancelled = h.Client.GetIssueAsync(h.Team, "DUN-1", DetailFields, impatient.Token);
        var patient = h.Client.GetIssueAsync(h.Team, "DUN-1", DetailFields, Ct);
        await h.WaitForRequestsAsync(1);
        await impatient.CancelAsync();
        gate.SetResult();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => cancelled);
        Assert.Equal("First", (await patient).Fields!.Summary);
        Assert.Single(h.Jira.Requests);
    }

    [Fact]
    public async Task Different_reads_are_not_shared() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.AddIssue("DUN-1", "First");
        h.Jira.AddIssue("DUN-2", "Second");

        await Task.WhenAll(
            h.Client.GetIssueAsync(h.Team, "DUN-1", DetailFields, Ct),
            h.Client.GetIssueAsync(h.Team, "DUN-2", DetailFields, Ct));

        Assert.Equal(2, h.Jira.Requests.Count);
    }

    [Fact]
    public async Task A_response_over_4_MiB_is_refused() {
        await using var h = await JiraHarness.StartAsync(postgres);
        var huge = "{\"accountId\":\"" + new string('a', 4 * 1024 * 1024) + "\"}";
        h.Jira.Script("GET", "/rest/api/3/myself", new FakeJiraResponse(200, huge));

        var error = await Assert.ThrowsAsync<JiraException>(() => h.Client.GetMyselfAsync(h.Team, Ct));

        Assert.Equal(JiraErrorCode.Unavailable, error.Error.Code);
    }

    [Fact]
    public async Task A_small_gzip_body_that_inflates_past_the_cap_is_refused() {
        await using var h = await JiraHarness.StartAsync(postgres);
        var json = Encoding.UTF8.GetBytes("{\"accountId\":\"" + new string('a', 5 * 1024 * 1024) + "\"}");
        using var compressed = new MemoryStream();
        await using (var gzip = new GZipStream(compressed, CompressionLevel.SmallestSize, leaveOpen: true)) {
            await gzip.WriteAsync(json, Ct);
        }
        Assert.True(compressed.Length < 64 * 1024);
        h.Jira.Script("GET", "/rest/api/3/myself", new FakeJiraResponse(
            200, Bytes: compressed.ToArray(), ContentType: "application/json", Headers: new Dictionary<string, string> { ["Content-Encoding"] = "gzip" }));

        var error = await Assert.ThrowsAsync<JiraException>(() => h.Client.GetMyselfAsync(h.Team, Ct));

        Assert.Equal(JiraErrorCode.Unavailable, error.Error.Code);
    }

    public static TheoryData<string[]> RefusedAnswers => new() {
        { ["10.0.0.5"] },
        { ["127.0.0.2"] },
        { ["169.254.169.254"] },
        { ["192.168.1.10"] },
        { ["100.64.0.1"] },
        { ["0.0.0.0"] },
        { ["::ffff:10.0.0.5"] },
        { ["fd00::1"] },
        { ["fe80::1"] },
        { ["8.8.8.8", "10.0.0.5"] },
    };

    [Theory]
    [MemberData(nameof(RefusedAnswers))]
    public async Task A_host_resolving_to_a_non_public_address_is_never_contacted(string[] answer) {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Resolver.Answer(JiraHarness.SiteHost, [.. answer.Select(IPAddress.Parse)]);

        var error = await Assert.ThrowsAsync<JiraException>(() => h.Client.GetMyselfAsync(h.Team, Ct));

        Assert.Equal(JiraErrorCode.Unavailable, error.Error.Code);
        Assert.Empty(h.Jira.Requests);
        Assert.Empty(h.Delays);
    }

    [Fact]
    public async Task The_production_address_rule_refuses_loopback() {
        await using var h = await JiraHarness.StartAsync(postgres, o => o with { IsAllowedAddress = IpClassifier.IsPublic });

        var error = await Assert.ThrowsAsync<JiraException>(() => h.Client.GetMyselfAsync(h.Team, Ct));

        Assert.Equal(JiraErrorCode.Unavailable, error.Error.Code);
        Assert.Empty(h.Jira.Requests);
    }

    [Fact]
    public async Task A_redirect_is_never_followed_for_api_calls() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.Script("GET", "/rest/api/3/myself", new FakeJiraResponse(
            302, Headers: new Dictionary<string, string> { ["Location"] = "https://api.media.atlassian.com/file/1/binary" }));

        var error = await Assert.ThrowsAsync<JiraException>(() => h.Client.GetMyselfAsync(h.Team, Ct));

        Assert.Equal(JiraErrorCode.Unavailable, error.Error.Code);
        Assert.Single(h.Jira.Requests);
    }

    [Fact]
    public async Task An_attachment_is_fetched_with_redirect_false() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.AddIssue("DUN-1", "First");
        byte[] png = [0x89, 0x50, 0x4e, 0x47, 1, 2, 3];
        h.Jira.AddAttachment("DUN-1", "10100", "shot.png", "image/png", png);

        var attachment = await h.Client.GetAttachmentAsync(h.Team, "10100", Ct);

        Assert.Equal("image/png", attachment.ContentType);
        Assert.Equal(png, attachment.Content);
        var request = Assert.Single(h.Jira.Requests);
        Assert.Equal("/rest/api/3/attachment/content/10100", request.Path);
        Assert.Equal("?redirect=false", request.Query);
    }

    [Fact]
    public async Task An_attachment_redirect_is_followed_once_to_the_media_host_without_credentials() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.AddIssue("DUN-1", "First");
        h.Jira.AddAttachment("DUN-1", "10100", "shot.webp", "image/webp", [1, 2, 3]);
        h.Jira.RedirectAttachments = true;

        var attachment = await h.Client.GetAttachmentAsync(h.Team, "10100", Ct);

        Assert.Equal("image/webp", attachment.ContentType);
        Assert.Equal(2, h.Jira.Requests.Count);
        var media = h.Jira.Requests[1];
        Assert.Equal(FakeJiraServer.MediaHost, media.Host);
        Assert.Equal("/file/10100/binary", media.Path);
        Assert.Null(media.Authorization);
        Assert.Contains(FakeJiraServer.MediaHost, h.Resolver.Lookups);
    }

    [Theory]
    [InlineData("https://evil.example.com/file/10100/binary?token=signed")]
    [InlineData("http://api.media.atlassian.com/file/10100/binary?token=signed")]
    [InlineData("https://api.media.atlassian.com:8443/file/10100/binary?token=signed")]
    [InlineData("https://user@api.media.atlassian.com/file/10100/binary?token=signed")]
    [InlineData("https://a.b.atlassian.net/file/10100/binary?token=signed")]
    [InlineData("https://api.media.atlassian.com.evil.com/file/10100/binary?token=signed")]
    [InlineData("https://127.0.0.1/file/10100/binary?token=signed")]
    [InlineData("/file/10100/binary?token=signed")]
    public async Task An_attachment_redirect_elsewhere_is_refused(string location) {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.AddIssue("DUN-1", "First");
        h.Jira.AddAttachment("DUN-1", "10100", "shot.png", "image/png", [1, 2, 3]);
        h.Jira.RedirectAttachments = true;
        h.Jira.AttachmentLocation = location;

        var error = await Assert.ThrowsAsync<JiraException>(() => h.Client.GetAttachmentAsync(h.Team, "10100", Ct));

        Assert.Equal(JiraErrorCode.Unavailable, error.Error.Code);
        Assert.Single(h.Jira.Requests);
    }

    [Fact]
    public async Task A_media_host_that_redirects_again_is_refused() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.AddIssue("DUN-1", "First");
        h.Jira.AddAttachment("DUN-1", "10100", "shot.png", "image/png", [1, 2, 3]);
        h.Jira.RedirectAttachments = true;
        h.Jira.Script("GET", "/file/", new FakeJiraResponse(
            302, Headers: new Dictionary<string, string> { ["Location"] = "https://api.media.atlassian.com/file/10100/binary?token=signed" }));

        var error = await Assert.ThrowsAsync<JiraException>(() => h.Client.GetAttachmentAsync(h.Team, "10100", Ct));

        Assert.Equal(JiraErrorCode.Unavailable, error.Error.Code);
        Assert.Equal(2, h.Jira.Requests.Count);
    }

    [Fact]
    public async Task A_media_host_on_a_private_address_is_refused() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.AddIssue("DUN-1", "First");
        h.Jira.AddAttachment("DUN-1", "10100", "shot.png", "image/png", [1, 2, 3]);
        h.Jira.RedirectAttachments = true;
        h.Resolver.Answer(FakeJiraServer.MediaHost, [IPAddress.Parse("10.1.2.3")]);

        var error = await Assert.ThrowsAsync<JiraException>(() => h.Client.GetAttachmentAsync(h.Team, "10100", Ct));

        Assert.Equal(JiraErrorCode.Unavailable, error.Error.Code);
        Assert.Single(h.Jira.Requests);
    }

    [Theory]
    [InlineData("image/svg+xml")]
    [InlineData("text/html")]
    [InlineData("application/octet-stream")]
    public async Task Attachments_that_are_not_raster_images_are_refused(string contentType) {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.AddIssue("DUN-1", "First");
        h.Jira.AddAttachment("DUN-1", "10100", "x", contentType, Encoding.UTF8.GetBytes("<svg onload=alert(1)/>"));

        var error = await Assert.ThrowsAsync<JiraException>(() => h.Client.GetAttachmentAsync(h.Team, "10100", Ct));

        Assert.Equal(JiraErrorCode.AttachmentUnsupported, error.Error.Code);
        Assert.Equal(415, error.Error.HttpStatus);
    }

    [Fact]
    public async Task Attachments_over_5_MiB_are_refused() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.AddIssue("DUN-1", "First");
        h.Jira.AddAttachment("DUN-1", "10100", "big.png", "image/png", new byte[5 * 1024 * 1024 + 1]);

        var error = await Assert.ThrowsAsync<JiraException>(() => h.Client.GetAttachmentAsync(h.Team, "10100", Ct));

        Assert.Equal(JiraErrorCode.AttachmentTooLarge, error.Error.Code);
    }

    [Theory]
    [InlineData(403, null, JiraErrorCode.Forbidden, 403)]
    [InlineData(404, null, JiraErrorCode.NotFound, 404)]
    [InlineData(500, null, JiraErrorCode.Unavailable, 503)]
    [InlineData(502, null, JiraErrorCode.Unavailable, 503)]
    [InlineData(409, null, JiraErrorCode.Validation, 422)]
    [InlineData(403, "AUTHENTICATION_DENIED", JiraErrorCode.AuthFailed, 502)]
    [InlineData(401, null, JiraErrorCode.AuthFailed, 502)]
    public async Task Jira_errors_map_to_stable_codes(int status, string? loginReason, string code, int httpStatus) {
        await using var h = await JiraHarness.StartAsync(postgres);
        var headers = loginReason is null ? null : new Dictionary<string, string> { ["X-Seraph-LoginReason"] = loginReason };
        h.Jira.Script("GET", "/rest/api/3/issue/DUN-9", new FakeJiraResponse(status, "{\"errorMessages\":[\"secret detail\"]}", headers));

        var error = await Assert.ThrowsAsync<JiraException>(() => h.Client.GetIssueAsync(h.Team, "DUN-9", DetailFields, Ct));

        Assert.Equal(code, error.Error.Code);
        Assert.Equal(httpStatus, error.Error.HttpStatus);
        Assert.DoesNotContain("secret", error.Message, StringComparison.Ordinal);
        Assert.Equal(code == JiraErrorCode.AuthFailed ? "auth_failed" : "ok", await h.StatusAsync(h.Team));
    }

    [Fact]
    public async Task Wrong_stored_credentials_mark_the_team_auth_failed() {
        await using var h = await JiraHarness.StartAsync(postgres);
        await h.ConfigureTeamAsync(h.Team, h.Jira.Email, "revoked-token");

        var error = await Assert.ThrowsAsync<JiraException>(() => h.Client.GetMyselfAsync(h.Team, Ct));

        Assert.Equal(JiraErrorCode.AuthFailed, error.Error.Code);
        Assert.Equal("auth_failed", await h.StatusAsync(h.Team));
        Assert.Equal(JiraConfigStatus.AuthFailed, (await h.Credentials.LoadAsync(h.Team, Ct))!.Status);
    }

    [Fact]
    public async Task A_400_carries_Jira_field_errors_as_plain_text() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.AddIssue("DUN-1", "First");
        var longMessage = new string('x', 500);
        h.Jira.Script("PUT", "/rest/api/3/issue/DUN-1", new FakeJiraResponse(
            400, $$$"""{"errorMessages":[],"errors":{"summary":"Summary is required.\u0007","customfield_10016":"{{{longMessage}}}"}}"""));

        var error = await Assert.ThrowsAsync<JiraException>(
            () => h.Client.UpdateIssueAsync(h.Team, "DUN-1", new JiraIssueUpdate(Summary: ""), Ct));

        Assert.Equal(JiraErrorCode.Validation, error.Error.Code);
        Assert.Equal(422, error.Error.HttpStatus);
        Assert.Equal("Summary is required. ", error.Error.Fields!["summary"]);
        Assert.Equal(JiraMap.FieldMessageMaxLength, error.Error.Fields["customfield_10016"].Length);
        Assert.Single(h.Jira.Requests);
    }

    [Fact]
    public async Task A_team_without_Jira_is_not_configured() {
        await using var h = await JiraHarness.StartAsync(postgres);

        var error = await Assert.ThrowsAsync<JiraException>(() => h.Client.GetMyselfAsync(new TeamId(Guid.NewGuid()), Ct));

        Assert.Equal(JiraErrorCode.NotConfigured, error.Error.Code);
        Assert.Equal(409, error.Error.HttpStatus);
        Assert.Empty(h.Jira.Requests);
    }

    [Fact]
    public async Task Probe_discovers_fields_and_the_scrum_board_with_unsaved_credentials() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.AddBoard(3, "DUN", type: "kanban");
        h.Jira.AddBoard(7, "DUN");

        var probe = await h.Client.ProbeAsync(h.Team, "https://ACME.atlassian.net/", h.Jira.Email, h.Jira.Token, "DUN", null, Ct);

        Assert.Equal("acc-bot", probe.Myself.AccountId);
        Assert.Equal(new JiraFieldIds("customfield_10016", "customfield_10020"), probe.Fields);
        Assert.Equal(7, probe.Board!.Id);
        Assert.Equal("DUN", probe.Board.Location!.ProjectKey);
    }

    [Fact]
    public async Task Probe_with_a_bad_token_fails_without_touching_the_stored_status() {
        await using var h = await JiraHarness.StartAsync(postgres);

        var error = await Assert.ThrowsAsync<JiraException>(
            () => h.Client.ProbeAsync(h.Team, JiraHarness.SiteHost, h.Jira.Email, "wrong", null, null, Ct));

        Assert.Equal(JiraErrorCode.AuthFailed, error.Error.Code);
        Assert.Equal("ok", await h.StatusAsync(h.Team));
    }

    [Theory]
    [InlineData("acme.atlassian.net.evil.com")]
    [InlineData("user@acme.atlassian.net")]
    [InlineData("10.0.0.1")]
    public async Task Probe_refuses_hosts_outside_the_policy(string host) {
        await using var h = await JiraHarness.StartAsync(postgres);

        var error = await Assert.ThrowsAsync<JiraException>(
            () => h.Client.ProbeAsync(h.Team, host, h.Jira.Email, h.Jira.Token, null, null, Ct));

        Assert.Equal(JiraErrorCode.Validation, error.Error.Code);
        Assert.Empty(h.Jira.Requests);
    }

    [Fact]
    public async Task Fields_are_cached_for_six_hours() {
        await using var h = await JiraHarness.StartAsync(postgres);

        await h.Client.GetFieldsAsync(h.Team, Ct);
        await h.Client.GetFieldsAsync(h.Team, Ct);
        h.Time.Advance(TimeSpan.FromHours(6));
        await h.Client.GetFieldsAsync(h.Team, Ct);

        Assert.Equal(2, h.Jira.Requests.Count);
    }

    [Fact]
    public async Task Points_are_written_through_the_configured_field_only() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.AddIssue("DUN-1", "First");

        await h.Client.SetPointsAsync(h.Team, "DUN-1", 2.5m, Ct);

        var body = JsonDocument.Parse(h.Jira.Requests.Single(r => r.Method == "PUT").Body);
        var fields = body.RootElement.GetProperty("fields");
        Assert.Equal(2.5m, fields.GetProperty("customfield_10016").GetDecimal());
        Assert.Single(fields.EnumerateObject());

        await h.Client.SetPointsAsync(h.Team, "DUN-1", null, Ct);
        Assert.Null(h.Jira.Issues["DUN-1"]["fields"]!["customfield_10016"]);
    }

    [Fact]
    public async Task Points_without_a_stored_or_discoverable_field_are_refused_before_writing() {
        await using var h = await JiraHarness.StartAsync(postgres);
        await h.ConfigureTeamAsync(h.Team, h.Jira.Email, h.Jira.Token, pointsFieldId: null);
        foreach (var points in h.Jira.Fields.Where(f => ((string?)f!["name"])?.Contains("Story point", StringComparison.OrdinalIgnoreCase) == true).ToList()) {
            h.Jira.Fields.Remove(points);
        }

        var error = await Assert.ThrowsAsync<JiraException>(() => h.Client.SetPointsAsync(h.Team, "DUN-1", 3, Ct));

        Assert.Equal(JiraErrorCode.NotConfigured, error.Error.Code);
        Assert.DoesNotContain(h.Jira.Requests, r => r.Method == "PUT");
    }

    [Fact]
    public async Task Search_pages_with_nextPageToken() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.AddIssue("DUN-1", "One");
        h.Jira.AddIssue("DUN-2", "Two");
        h.Jira.AddIssue("DUN-3", "Three");
        var jql = $"key in (\"DUN-1\",\"DUN-2\",\"DUN-3\") AND {JiraMap.JqlFieldRef("customfield_10016")} is EMPTY";

        var keys = new List<string>();
        string? token = null;
        do {
            var page = await h.Client.SearchAsync(h.Team, jql, ["summary"], 2, token, Ct);
            keys.AddRange(page.Issues!.Select(i => i.Key!));
            token = page.IsLast == true ? null : page.NextPageToken;
        } while (token is not null);

        Assert.Equal(["DUN-1", "DUN-2", "DUN-3"], keys);
        Assert.Equal(2, h.Jira.Requests.Count);
        Assert.Contains("cf[10016] is EMPTY", h.Jira.Requests[0].Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Sprints_are_read_across_pages_and_ordered_like_the_browse_buckets() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.AddBoard(7, "DUN");
        for (var i = 1; i <= 60; i++) {
            h.Jira.AddSprint(i, $"Sprint {61 - i}", i % 2 == 0 ? "active" : "future", null);
        }
        h.Jira.AddSprint(100, "Sprint 100", "closed", "2026-01-01T00:00:00.000Z");
        h.Jira.AddSprint(101, "Later", "future", "2026-12-01T00:00:00.000Z");
        h.Jira.AddSprint(102, "Sooner", "future", "2026-11-01T00:00:00.000+10:00");

        var sprints = await h.Client.GetSprintsAsync(h.Team, 7, Ct);

        Assert.Equal(62, sprints.Count);
        Assert.Equal(["Sooner", "Later", "Sprint 1", "Sprint 2"], sprints.Take(4).Select(s => s.Name));
        Assert.Equal("Sprint 60", sprints[^1].Name);
        Assert.Equal(2, h.Jira.Requests.Count);
    }

    [Fact]
    public async Task Recent_sprints_of_a_young_board_are_its_active_sprints_then_every_closed_one() {
        await using var h = await JiraHarness.StartAsync(postgres);
        AddClosedSprints(h, 12);
        h.Jira.AddSprint(13, "Sprint 13", "active", "2026-10-12T09:00:00.000Z");
        h.Jira.AddSprint(14, "Sprint 14", "future", null);

        var sprints = await h.Client.GetRecentSprintsAsync(h.Team, 7, 30, Ct);

        Assert.Equal([13L, .. Enumerable.Range(1, 12).Select(i => (long)i)], sprints.Select(s => s.Id));
        Assert.Equal(["state=active&startAt=0", "state=closed&startAt=0"], SprintQueries(h));
    }

    [Fact]
    public async Task Recent_sprints_jump_to_the_tail_when_Jira_sends_a_total() {
        await using var h = await JiraHarness.StartAsync(postgres);
        AddClosedSprints(h, 200);

        var sprints = await h.Client.GetRecentSprintsAsync(h.Team, 7, 30, Ct);

        Assert.Equal(Enumerable.Range(171, 30).Select(i => (long)i), sprints.Select(s => s.Id));
        Assert.Equal(["state=active&startAt=0", "state=closed&startAt=0", "state=closed&startAt=170"], SprintQueries(h));
    }

    [Fact]
    public async Task The_sprint_reports_100_recent_sprints_cost_four_calls_on_an_old_board() {
        await using var h = await JiraHarness.StartAsync(postgres);
        AddClosedSprints(h, 250);

        var sprints = await h.Client.GetRecentSprintsAsync(h.Team, 7, SprintReportService.PickerSize, Ct);

        Assert.Equal(Enumerable.Range(151, 100).Select(i => (long)i), sprints.Select(s => s.Id));
        Assert.Equal(
            ["state=active&startAt=0", "state=closed&startAt=0", "state=closed&startAt=150", "state=closed&startAt=200"],
            SprintQueries(h));
    }

    [Fact]
    public async Task Recent_sprints_page_forward_keeping_the_newest_without_a_total() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.OmitTotals = true;
        AddClosedSprints(h, 120);

        var sprints = await h.Client.GetRecentSprintsAsync(h.Team, 7, 30, Ct);

        Assert.Equal(Enumerable.Range(91, 30).Select(i => (long)i), sprints.Select(s => s.Id));
        Assert.Equal(
            ["state=active&startAt=0", "state=closed&startAt=0", "state=closed&startAt=50", "state=closed&startAt=100"],
            SprintQueries(h));
    }

    [Fact]
    public async Task A_stale_total_that_jumps_past_the_end_falls_back_to_paging_forward() {
        await using var h = await JiraHarness.StartAsync(postgres);
        AddClosedSprints(h, 80);
        var firstPage = new System.Text.Json.Nodes.JsonObject {
            ["startAt"] = 0,
            ["maxResults"] = 50,
            ["isLast"] = false,
            ["total"] = 500,
            ["values"] = new System.Text.Json.Nodes.JsonArray([.. h.Jira.Sprints.Take(50).Select(s => s.DeepClone())]),
        };
        h.Jira.Script(
            "GET",
            "/rest/agile/1.0/board/7/sprint",
            new FakeJiraResponse(200, """{"startAt":0,"maxResults":50,"isLast":true,"values":[]}"""),
            new FakeJiraResponse(200, firstPage.ToJsonString()));

        var sprints = await h.Client.GetRecentSprintsAsync(h.Team, 7, 30, Ct);

        Assert.Equal(Enumerable.Range(51, 30).Select(i => (long)i), sprints.Select(s => s.Id));
        Assert.Equal(
            ["state=active&startAt=0", "state=closed&startAt=0", "state=closed&startAt=470", "state=closed&startAt=50"],
            SprintQueries(h));
    }

    [Fact]
    public async Task Recent_sprints_stop_after_30_calls() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.OmitTotals = true;
        AddClosedSprints(h, 2000);

        var sprints = await h.Client.GetRecentSprintsAsync(h.Team, 7, 30, Ct);

        Assert.Equal(30, h.Jira.Requests.Count);
        Assert.Equal(Enumerable.Range(1421, 30).Select(i => (long)i), sprints.Select(s => s.Id));
    }

    [Fact]
    public async Task Recent_sprints_carry_their_close_date_goal_and_owning_board() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.AddSprint(5, "Sprint 5", "closed", "2026-09-28T09:00:00.000Z", "2026-10-12T09:00:00.000Z", "2026-10-12T16:30:00.000Z", "Ship it", 7);

        var sprint = Assert.Single(await h.Client.GetRecentSprintsAsync(h.Team, 7, 30, Ct));

        Assert.Equal(
            new JiraSprint(5, "Sprint 5", "closed", "2026-09-28T09:00:00.000Z", "2026-10-12T09:00:00.000Z", "2026-10-12T16:30:00.000Z", "Ship it", 7),
            sprint);
    }

    [Fact]
    public async Task A_boards_estimation_field_is_read_from_its_configuration_and_cached() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.AddBoard(7, "DUN");
        h.Jira.BoardEstimation = FakeJiraServer.FieldEstimation(BoardField, "Story Points");

        Assert.Equal(new JiraBoardEstimation.Field(BoardField), await h.Client.GetBoardEstimationAsync(h.Team, 7, Ct));
        Assert.Equal(new JiraBoardEstimation.Field(BoardField), await h.Client.GetBoardEstimationAsync(h.Team, 7, Ct));

        var request = Assert.Single(h.Jira.Requests);
        Assert.Equal("/rest/agile/1.0/board/7/configuration", request.Path);
    }

    [Theory]
    [InlineData("""{"type":"issueCount"}""")]
    [InlineData("""{"type":"none"}""")]
    [InlineData("""{"type":"field","field":{"fieldId":"timeoriginalestimate"}}""")]
    [InlineData("""{"type":"field"}""")]
    [InlineData(null)]
    public async Task A_board_estimating_without_a_custom_field_has_no_estimation_field(string? estimation) {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.AddBoard(7, "DUN");
        h.Jira.BoardEstimation = estimation is null ? null : System.Text.Json.Nodes.JsonNode.Parse(estimation)!.AsObject();

        Assert.IsType<JiraBoardEstimation.None>(await h.Client.GetBoardEstimationAsync(h.Team, 7, Ct));
    }

    [Theory]
    [InlineData(404)]
    [InlineData(403)]
    public async Task A_board_that_is_gone_or_hidden_is_remembered_without_a_field_for_five_minutes(int status) {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.AddBoard(7, "DUN");
        h.Jira.Script("GET", "/rest/agile/1.0/board/7/configuration", new FakeJiraResponse(status, "{}"));

        Assert.IsType<JiraBoardEstimation.None>(await h.Client.GetBoardEstimationAsync(h.Team, 7, Ct));
        h.Time.Advance(TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1));
        Assert.IsType<JiraBoardEstimation.None>(await h.Client.GetBoardEstimationAsync(h.Team, 7, Ct));
        Assert.Single(h.Jira.Requests);

        h.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new JiraBoardEstimation.Field("customfield_10016"), await h.Client.GetBoardEstimationAsync(h.Team, 7, Ct));
        Assert.Equal(2, h.Jira.Requests.Count);
    }

    [Fact]
    public async Task A_board_configuration_that_cannot_be_read_for_now_is_remembered_for_thirty_seconds() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.AddBoard(7, "DUN");
        h.Jira.Script("GET", "/rest/agile/1.0/board/7/configuration", new FakeJiraResponse(500, "{}"));

        var failed = Assert.IsType<JiraBoardEstimation.Unknown>(await h.Client.GetBoardEstimationAsync(h.Team, 7, Ct));
        Assert.Equal(JiraErrorCode.Unavailable, failed.Error.Code);
        h.Time.Advance(TimeSpan.FromSeconds(29));
        Assert.IsType<JiraBoardEstimation.Unknown>(await h.Client.GetBoardEstimationAsync(h.Team, 7, Ct));
        Assert.Single(h.Jira.Requests);

        h.Time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(new JiraBoardEstimation.Field("customfield_10016"), await h.Client.GetBoardEstimationAsync(h.Team, 7, Ct));
        Assert.Equal(2, h.Jira.Requests.Count);
    }

    [Fact]
    public async Task A_settings_change_drops_a_remembered_board_failure() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.AddBoard(7, "DUN");
        h.Jira.Script("GET", "/rest/agile/1.0/board/7/configuration", new FakeJiraResponse(403, "{}"));
        await h.Client.GetBoardEstimationAsync(h.Team, 7, Ct);

        h.Cache.InvalidateTag(JiraCache.TeamTag(h.Team));

        Assert.Equal(new JiraBoardEstimation.Field("customfield_10016"), await h.Client.GetBoardEstimationAsync(h.Team, 7, Ct));
        Assert.Equal(2, h.Jira.Requests.Count);
    }

    [Fact]
    public async Task A_burst_of_reads_against_a_hidden_board_costs_one_configuration_request() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.AddBoard(7, "DUN");
        h.Jira.Script("GET", "/rest/agile/1.0/board/7/configuration", new FakeJiraResponse(403, "{}"));

        for (var i = 0; i < 5; i++) {
            Assert.Equal("customfield_10016", (await h.Client.EffectiveConfigAsync(h.Team, Ct)).PointsFieldId);
        }

        Assert.Single(h.Jira.Requests, r => r.Path == "/rest/agile/1.0/board/7/configuration");
    }

    [Theory]
    [InlineData(500, null, JiraErrorCode.Unavailable)]
    [InlineData(429, "60", JiraErrorCode.RateLimited)]
    public async Task A_points_write_fails_with_the_error_when_the_board_configuration_cannot_be_read_for_now(int status, string? retryAfter, string code) {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.AddBoard(7, "DUN");
        h.Jira.BoardEstimation = FakeJiraServer.FieldEstimation(BoardField, "Story Points");
        h.Jira.EditableFields.Add(BoardField);
        h.Jira.AddIssue("DUN-1", "First");
        var headers = retryAfter is null ? null : new Dictionary<string, string> { ["Retry-After"] = retryAfter };
        h.Jira.Script("GET", "/rest/agile/1.0/board/7/configuration", new FakeJiraResponse(status, "{}", headers));

        var error = await Assert.ThrowsAsync<JiraException>(() => h.Client.SetPointsAsync(h.Team, "DUN-1", 3, Ct));

        Assert.Equal(code, error.Error.Code);
        Assert.DoesNotContain(h.Jira.Requests, r => r.Method == "PUT");
    }

    [Fact]
    public async Task A_points_write_reads_a_board_failure_a_read_remembered_again() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.AddBoard(7, "DUN");
        h.Jira.BoardEstimation = FakeJiraServer.FieldEstimation(BoardField, "Story Points");
        h.Jira.EditableFields.Add(BoardField);
        h.Jira.AddIssue("DUN-1", "First");
        h.Jira.Script("GET", "/rest/agile/1.0/board/7/configuration", new FakeJiraResponse(500, "{}"));
        Assert.Equal("customfield_10016", (await h.Client.EffectiveConfigAsync(h.Team, Ct)).PointsFieldId);

        await h.Client.SetPointsAsync(h.Team, "DUN-1", 3, Ct);

        Assert.Equal(3m, (decimal?)h.Jira.Issues["DUN-1"]["fields"]![BoardField]);
        Assert.Null(h.Jira.Issues["DUN-1"]["fields"]!["customfield_10016"]);
        Assert.Equal(2, h.Jira.Requests.Count(r => r.Path == "/rest/agile/1.0/board/7/configuration"));
    }

    [Theory]
    [InlineData(404)]
    [InlineData(403)]
    public async Task A_points_write_for_a_board_that_is_gone_or_hidden_uses_the_stored_field(int status) {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.AddBoard(7, "DUN");
        h.Jira.BoardEstimation = FakeJiraServer.FieldEstimation(BoardField, "Story Points");
        h.Jira.EditableFields.Add(BoardField);
        h.Jira.AddIssue("DUN-1", "First");
        h.Jira.Script("GET", "/rest/agile/1.0/board/7/configuration", new FakeJiraResponse(status, "{}"));

        await h.Client.SetPointsAsync(h.Team, "DUN-1", 3, Ct);

        Assert.Equal(3m, (decimal?)h.Jira.Issues["DUN-1"]["fields"]!["customfield_10016"]);
        Assert.Null(h.Jira.Issues["DUN-1"]["fields"]![BoardField]);
    }

    [Fact]
    public async Task A_field_catalogue_that_cannot_be_read_leaves_reads_without_points_and_fails_writes() {
        await using var h = await JiraHarness.StartAsync(postgres);
        await h.ConfigureTeamAsync(h.Team, h.Jira.Email, h.Jira.Token, pointsFieldId: null);
        h.Jira.AddBoard(7, "DUN");
        h.Jira.BoardEstimation = null;
        h.Jira.AddIssue("DUN-1", "First");
        h.Jira.Script("GET", "/rest/api/3/field", new FakeJiraResponse(500, "{}"), new FakeJiraResponse(500, "{}"));

        Assert.Null((await h.Client.EffectiveConfigAsync(h.Team, Ct)).PointsFieldId);
        var error = await Assert.ThrowsAsync<JiraException>(() => h.Client.SetPointsAsync(h.Team, "DUN-1", 3, Ct));

        Assert.Equal(JiraErrorCode.Unavailable, error.Error.Code);
        Assert.DoesNotContain(h.Jira.Requests, r => r.Method == "PUT");
    }

    [Fact]
    public async Task Ranked_pages_and_writes_reach_the_verified_paths() {
        await using var h = await JiraHarness.StartAsync(postgres);
        h.Jira.AddBoard(7, "DUN");
        h.Jira.AddSprint(5, "Sprint 5", "active", null);
        h.Jira.AddIssue("DUN-1", "In sprint", sprintId: 5);
        h.Jira.AddIssue("DUN-2", "Backlog");

        var sprintPage = await h.Client.GetSprintIssuesAsync(h.Team, 5, "statusCategory != \"Done\"", ["summary"], 0, Ct);
        var backlog = await h.Client.GetBacklogAsync(h.Team, 7, null, ["summary"], 0, Ct);
        await h.Client.MoveToSprintAsync(h.Team, 5, ["DUN-2"], Ct);
        await h.Client.MoveToBacklogAsync(h.Team, 7, ["DUN-1"], Ct);
        await h.Client.TransitionAsync(h.Team, "DUN-1", "21", Ct);
        var commentId = await h.Client.AddCommentAsync(h.Team, "DUN-1", JsonDocument.Parse("""{"type":"doc","version":1,"content":[]}""").RootElement, Ct);
        await h.Client.CreateLinkAsync(h.Team, "Blocks", "DUN-1", "DUN-2", Ct);
        await h.Client.DeleteLinkAsync(h.Team, "10001", Ct);
        var types = await h.Client.GetCreateIssueTypesAsync(h.Team, "DUN", Ct);
        var transitions = await h.Client.GetTransitionsAsync(h.Team, "DUN-1", Ct);
        var editMeta = await h.Client.GetEditMetaAsync(h.Team, "DUN-1", Ct);
        var linkTypes = await h.Client.GetLinkTypesAsync(h.Team, Ct);
        var users = await h.Client.SearchAssignableAsync(h.Team, "DUN-1", "ad a", Ct);
        var comments = await h.Client.GetCommentsAsync(h.Team, "DUN-1", 0, 20, Ct);
        var project = await h.Client.GetProjectAsync(h.Team, "DUN", Ct);

        Assert.Equal("DUN-1", Assert.Single(sprintPage.Issues!).Key);
        Assert.Equal("DUN-2", Assert.Single(backlog.Issues!).Key);
        Assert.Equal("Sprint 5", JiraMap.ReadSprint(await h.Client.GetIssueAsync(h.Team, "DUN-2", ["customfield_10020"], Ct), "customfield_10020")!.Name);
        Assert.Null(h.Jira.Issues["DUN-1"]["fields"]!["customfield_10020"]);
        Assert.NotNull(commentId);
        Assert.Equal(["Story", "Sub-task"], types.Select(t => t.Name));
        Assert.Equal(["21", "31"], transitions.Select(t => t.Id));
        Assert.Equal(2, editMeta.Fields!.Priority!.AllowedValues!.Count);
        Assert.Equal("Blocks", Assert.Single(linkTypes).Name);
        Assert.Equal("Ada Lovelace", Assert.Single(users).DisplayName);
        Assert.Equal(0, comments.Total);
        Assert.Equal("DUN", project.Key);

        var paths = h.Jira.Requests.Select(r => $"{r.Method} {r.Path}").ToList();
        Assert.Contains("POST /rest/agile/1.0/sprint/5/issue", paths);
        Assert.Contains("POST /rest/agile/1.0/backlog/7/issue", paths);
        Assert.Contains("POST /rest/api/3/issue/DUN-1/transitions", paths);
        Assert.Contains("GET /rest/api/3/issue/createmeta/DUN/issuetypes", paths);
        Assert.Contains("DELETE /rest/api/3/issueLink/10001", paths);
        Assert.Contains(h.Jira.Requests, r => r.Path.EndsWith("/comment", StringComparison.Ordinal) && r.Query.Contains("orderBy=-created", StringComparison.Ordinal));
        Assert.Contains(h.Jira.Requests, r => r.Path == "/rest/api/3/user/assignable/search" && r.Query.Contains("query=ad%20a", StringComparison.Ordinal));
    }

    private static void AddClosedSprints(JiraHarness h, int count) {
        h.Jira.AddBoard(7, "DUN");
        for (var i = 1; i <= count; i++) {
            h.Jira.AddSprint(i, $"Sprint {i}", "closed", null);
        }
    }

    private static List<string> SprintQueries(JiraHarness h) =>
        [.. h.Jira.Requests.Where(r => r.Path == "/rest/agile/1.0/board/7/sprint").Select(r => r.Query.TrimStart('?').Replace("&maxResults=50", "", StringComparison.Ordinal))];

    [Theory]
    [InlineData("../myself")]
    [InlineData("DUN-1/../../myself")]
    [InlineData("dun-1")]
    [InlineData("DUN-1?x=1")]
    public async Task Malformed_issue_keys_never_reach_Jira(string key) {
        await using var h = await JiraHarness.StartAsync(postgres);

        await Assert.ThrowsAsync<ArgumentException>(() => h.Client.GetIssueAsync(h.Team, key, ["summary"], Ct));

        Assert.Empty(h.Jira.Requests);
    }

    [Fact]
    public async Task Credentials_rotate_to_the_active_key_and_keep_working() {
        var oldKeys = JiraHarness.NewKeys("k1");
        await using var h = await JiraHarness.StartAsync(postgres, appKeys: oldKeys, ownDatabase: true);
        var team = h.Team;
        var rotated = Retroscope.Core.Security.Keyring.Parse($"{JiraHarness.NewKeys("k2")},{oldKeys}");
        var credentials = new Credentials(rotated, h.DataSource, h.Time);

        var before = await credentials.LoadAsync(team, Ct);
        Assert.Equal("k1", before!.KeyId);
        Assert.False(rotated.IsSealedWithActiveKey(before.CredentialsBox));

        var result = await credentials.RotateAsync(Ct);

        Assert.Equal((1, 0), result);
        var after = await credentials.LoadAsync(team, Ct);
        Assert.Equal("k2", after!.KeyId);
        Assert.True(rotated.IsSealedWithActiveKey(after.CredentialsBox));
        Assert.Equal(h.Jira.BasicAuthorization, "Basic " + credentials.TryCreateBasicParameter(team, after.CredentialsBox));
        Assert.Null(h.Credentials.TryCreateBasicParameter(team, after.CredentialsBox));
        Assert.Equal((0, 0), await credentials.RotateAsync(Ct));
    }
}
