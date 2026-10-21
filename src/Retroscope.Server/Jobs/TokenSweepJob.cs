using Npgsql;
using Retroscope.Server.Auth;

namespace Retroscope.Server.Jobs;

public sealed record TokenSweepReport(int Sessions, int GuestTokens, int AuthTokens, int Invites);

/// <summary>
/// Hourly sweep of credentials that can no longer be used (§b retention): expired sign-in sessions, guest tokens and
/// email tokens, and revoked or expired team invites. Swept sessions are also evicted from the session cache and
/// their sockets closed, as an explicit revocation would.
/// </summary>
public sealed partial class TokenSweepJob(
    NpgsqlDataSource dataSource,
    TimeProvider time,
    IServiceProvider services,
    ILogger<TokenSweepJob> logger) : ScheduledJob(time, services, logger) {
    protected override TimeSpan Interval => TimeSpan.FromHours(1);

    public override Task RunOnceAsync(CancellationToken cancellationToken) => RunAsync(cancellationToken);

    public async Task<TokenSweepReport> RunAsync(CancellationToken cancellationToken) {
        var now = Time.GetUtcNow();

        var sessions = new List<byte[]>();
        await using (var command = dataSource.CreateCommand(
            "delete from sessions where idle_expires_at <= $1 or abs_expires_at <= $1 returning id_hash")) {
            command.Parameters.Add(Parameter(now));
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) {
                sessions.Add(reader.GetFieldValue<byte[]>(0));
            }
        }
        Services.GetService<Sessions>()?.Revoked(sessions);

        var report = new TokenSweepReport(
            sessions.Count,
            await ExecuteAsync("delete from guest_tokens where expires_at <= $1", now, cancellationToken),
            await ExecuteAsync("delete from auth_tokens where expires_at <= $1", now, cancellationToken),
            await ExecuteAsync(
                "delete from team_invites where revoked_at is not null or expires_at <= $1", now, cancellationToken));
        LogRan(Logger, report);
        return report;
    }

    private async Task<int> ExecuteAsync(string sql, DateTimeOffset now, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand(sql);
        command.Parameters.Add(Parameter(now));
        return await command.ExecuteNonQueryAsync(cancellationToken);
    }

    [LoggerMessage(1, LogLevel.Information, "Token sweep: {Report}")]
    private static partial void LogRan(ILogger logger, TokenSweepReport report);
}
