using System.Security.Cryptography;
using System.Text.Json;
using Retroscope.Core.Security;
using Retroscope.Importer.Map;

namespace Retroscope.Importer.Verify;

/// <summary>
/// Every <c>team_jira_config</c> box opens with the active production key and holds the server's
/// <c>{"email","token"}</c> document (§h.5), so the app can use the credentials after the restore.
/// </summary>
public sealed class JiraConfigCheck : IVerifyCheck {
    public string Name => "jira-config";

    public async Task<IReadOnlyList<string>> RunAsync(VerifyContext context, CancellationToken cancellationToken) {
        var rows = await TargetQueries.RowsAsync(context.Target,
            "select team_id, credentials_box, key_id from team_jira_config order by team_id", cancellationToken);
        if (rows.Count == 0) {
            return [];
        }
        if (context.Keys is not { Keyring: var keyring }) {
            return ["APP_KEYS is needed to open the Jira credentials"];
        }

        var failures = new List<string>();
        foreach (var row in rows) {
            var (team, box, keyId) = ((Guid)row[0]!, (byte[])row[1]!, (string)row[2]!);
            if (!keyring.IsSealedWithActiveKey(box) || keyId != keyring.ActiveKeyId) {
                failures.Add($"team {team}: the box is not sealed with the active key {keyring.ActiveKeyId}");
            } else if (!keyring.TryOpen(KeyPurpose.JiraCredentials, box, TeamsMap.JiraAssociatedData(team), out var plaintext)) {
                failures.Add($"team {team}: the box does not open");
            } else {
                try {
                    if (!HasCredentials(plaintext)) {
                        failures.Add($"team {team}: the box does not hold an email and a token");
                    }
                } finally {
                    CryptographicOperations.ZeroMemory(plaintext);
                }
            }
        }
        return TargetQueries.Summarize(failures);
    }

    private static bool HasCredentials(byte[] plaintext) {
        try {
            using var document = JsonDocument.Parse(plaintext);
            return document.RootElement.ValueKind == JsonValueKind.Object
                && document.RootElement.TryGetProperty("email", out var email) && email.ValueKind == JsonValueKind.String
                && document.RootElement.TryGetProperty("token", out var token) && token.ValueKind == JsonValueKind.String;
        } catch (JsonException) {
            return false;
        }
    }
}
