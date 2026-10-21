using Retroscope.Core.Room;

namespace Retroscope.Server.Jira;

/// <summary>
/// The operator's key rotation (RUNBOOK): after a deploy that puts a new key first in <c>APP_KEYS</c>, the leader
/// re-seals every Jira credential box under it once at start-up, so the old key can later be dropped. Rows already
/// under the active key are skipped, so a restart without a new key does nothing. Runs after <see cref="Data.Leader"/>
/// has started, that is after the migrations.
/// </summary>
public sealed partial class CredentialRotation(IServiceProvider services, IConfiguration configuration, ILogger<CredentialRotation> logger)
    : BackgroundService {
    protected override async Task ExecuteAsync(CancellationToken stoppingToken) {
        // Only a keyring that still lists a retired key has anything to re-seal.
        if (configuration["APP_KEYS"]?.Contains(',', StringComparison.Ordinal) != true
            || services.GetService<ILeaderEpoch>() is not { IsLeader: true }) {
            return;
        }

        try {
            var (resealed, unopenable) = await services.GetRequiredService<Credentials>().RotateAsync(stoppingToken);
            if (resealed > 0 || unopenable > 0) {
                LogRotated(logger, resealed, unopenable);
            }
        } catch (Exception e) when (e is not OperationCanceledException) {
            LogFailed(logger, e);
        }
    }

    [LoggerMessage(1, LogLevel.Information, "Re-sealed {Resealed} Jira credential boxes under the active key; {Unopenable} could not be opened by any key")]
    private static partial void LogRotated(ILogger logger, int resealed, int unopenable);

    [LoggerMessage(2, LogLevel.Error, "Re-sealing Jira credentials under the active key failed; the next start retries")]
    private static partial void LogFailed(ILogger logger, Exception exception);
}
