using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Security;

namespace Retroscope.Server.Jira;

public enum JiraConfigStatus {
    Ok,
    NeedsTest,
    AuthFailed,
    Unreachable,
    RotateRequired,
}

/// <summary>A team's <c>team_jira_config</c> row. The credentials stay sealed; only <see cref="Credentials"/> opens them.</summary>
public sealed record JiraTeamConfig(
    TeamId Team,
    string SiteHost,
    byte[] CredentialsBox,
    string KeyId,
    string TokenLast4,
    string EmailMasked,
    string? ProjectKey,
    long? BoardId,
    string? TicketPrefix,
    string? PointsFieldId,
    bool PointsFieldOverridden,
    string? SprintFieldId,
    JiraConfigStatus Status,
    bool MembersCanEdit,
    DateTimeOffset? LastCheckedAt);

/// <summary>What the config writer stores for a newly entered token; the box names its key, so <c>key_id</c> is read from it.</summary>
public sealed record SealedJiraCredentials(byte[] Box, string KeyId, string TokenLast4, string EmailMasked);

/// <summary>
/// Seals and opens team Jira credentials (DESIGN §e/§f) and reads the team's config row. The box is
/// <c>Keyring.Seal(JiraCredentials, {"email","token"}, AAD = "jira:v1:" ‖ team id)</c>. A token is opened per request
/// into a pooled buffer, turned into the Basic header value and cleared; it is never logged.
/// </summary>
public sealed class Credentials {
    private const string ConfigColumns = """
        team_id, site_host, credentials_box, key_id, token_last4, email_masked, project_key, board_id, ticket_prefix,
        points_field_id, points_field_overridden, sprint_field_id, status, members_can_edit, last_checked_at
        """;

    private static readonly TimeSpan ConfigTtl = TimeSpan.FromSeconds(30);

    private readonly Keyring _keyring;
    private readonly NpgsqlDataSource _dataSource;
    private readonly TimeProvider _time;
    private readonly ConcurrentDictionary<TeamId, (JiraTeamConfig? Config, DateTimeOffset LoadedAt)> _configs = new();

    public Credentials(Keyring keyring, NpgsqlDataSource dataSource, TimeProvider time) {
        _keyring = keyring;
        _dataSource = dataSource;
        _time = time;
    }

    public static byte[] AssociatedData(TeamId team) {
        var aad = new byte[8 + 16];
        "jira:v1:"u8.CopyTo(aad);
        team.Value.TryWriteBytes(aad.AsSpan(8), bigEndian: true, out _);
        return aad;
    }

    public SealedJiraCredentials Seal(TeamId team, string email, string token) {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        if (token.Length < 4) {
            throw new ArgumentException("The token is too short.", nameof(token));
        }

        var buffer = new ArrayBufferWriter<byte>(256);
        byte[] box;
        try {
            using (var writer = new Utf8JsonWriter(buffer)) {
                writer.WriteStartObject();
                writer.WriteString("email"u8, email);
                writer.WriteString("token"u8, token);
                writer.WriteEndObject();
            }
            box = _keyring.Seal(KeyPurpose.JiraCredentials, buffer.WrittenSpan, AssociatedData(team));
        } finally {
            CryptographicOperations.ZeroMemory(MemoryMarshal.AsMemory(buffer.WrittenMemory).Span);
        }

        Keyring.TryReadKeyId(box, out var keyId);
        return new SealedJiraCredentials(box, keyId!, token[^4..], MaskEmail(email));
    }

    /// <summary>The <c>base64(email:token)</c> parameter of a Basic Authorization header, or null when the box does not open.</summary>
    public string? TryCreateBasicParameter(TeamId team, ReadOnlySpan<byte> box) {
        var length = Keyring.PlaintextLength(box);
        if (length <= 0) {
            return null;
        }

        var plaintext = ArrayPool<byte>.Shared.Rent(length);
        var pair = ArrayPool<byte>.Shared.Rent(length);
        try {
            if (!_keyring.TryOpen(KeyPurpose.JiraCredentials, box, AssociatedData(team), plaintext.AsSpan(0, length))) {
                return null;
            }

            var written = WriteBasicPair(plaintext.AsSpan(0, length), pair);
            return written < 0 ? null : Convert.ToBase64String(pair, 0, written);
        } finally {
            CryptographicOperations.ZeroMemory(plaintext.AsSpan(0, length));
            CryptographicOperations.ZeroMemory(pair.AsSpan(0, length));
            ArrayPool<byte>.Shared.Return(plaintext);
            ArrayPool<byte>.Shared.Return(pair);
        }
    }

    public static string CreateBasicParameter(string email, string token) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes($"{email}:{token}"));

    /// <summary>The team's config, cached for 30 s; null when the team has no Jira configured.</summary>
    public async ValueTask<JiraTeamConfig?> LoadAsync(TeamId team, CancellationToken cancellationToken) {
        var now = _time.GetUtcNow();
        if (_configs.TryGetValue(team, out var cached) && now - cached.LoadedAt < ConfigTtl) {
            return cached.Config;
        }

        await using var command = _dataSource.CreateCommand($"select {ConfigColumns} from team_jira_config where team_id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = team.Value });
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var config = await reader.ReadAsync(cancellationToken) ? ReadConfig(reader) : null;
        _configs[team] = (config, now);
        return config;
    }

    /// <summary>Call after writing the team's config row, so the next request reads it.</summary>
    public void Invalidate(TeamId team) => _configs.TryRemove(team, out _);

    /// <summary>Records the outcome of a test or of an authentication failure seen by the proxy.</summary>
    public async Task MarkStatusAsync(TeamId team, JiraConfigStatus status, CancellationToken cancellationToken) {
        await using var command = _dataSource.CreateCommand(
            "update team_jira_config set status = $2, last_checked_at = now() where team_id = $1");
        command.Parameters.Add(new NpgsqlParameter { Value = team.Value });
        command.Parameters.Add(new NpgsqlParameter { Value = StatusText(status) });
        await command.ExecuteNonQueryAsync(cancellationToken);
        Invalidate(team);
    }

    /// <summary>
    /// The key-rotation step for Jira credentials: re-seals every box not sealed with the active key and updates
    /// <c>key_id</c>. Boxes no key opens are left alone and counted in <c>Unopenable</c>.
    /// </summary>
    public async Task<(int Resealed, int Unopenable)> RotateAsync(CancellationToken cancellationToken) {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        var rows = new List<(Guid Team, byte[] Box)>();
        await using (var select = new NpgsqlCommand(
            "select team_id, credentials_box from team_jira_config where key_id <> $1 for update", connection, transaction)) {
            select.Parameters.Add(new NpgsqlParameter { Value = _keyring.ActiveKeyId });
            await using var reader = await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken)) {
                rows.Add((reader.GetGuid(0), (byte[])reader.GetValue(1)));
            }
        }

        int resealed = 0, unopenable = 0;
        foreach (var (team, box) in rows) {
            if (_keyring.IsSealedWithActiveKey(box)) {
                continue;
            }
            if (!_keyring.TryReseal(KeyPurpose.JiraCredentials, box, AssociatedData(new TeamId(team)), out var newBox)) {
                unopenable++;
                continue;
            }

            Keyring.TryReadKeyId(newBox, out var keyId);
            await using var update = new NpgsqlCommand(
                "update team_jira_config set credentials_box = $2, key_id = $3 where team_id = $1", connection, transaction);
            update.Parameters.Add(new NpgsqlParameter { Value = team });
            update.Parameters.Add(new NpgsqlParameter { Value = newBox });
            update.Parameters.Add(new NpgsqlParameter { Value = keyId! });
            await update.ExecuteNonQueryAsync(cancellationToken);
            resealed++;
        }

        await transaction.CommitAsync(cancellationToken);
        _configs.Clear();
        return (resealed, unopenable);
    }

    public static string MaskEmail(string email) {
        ArgumentNullException.ThrowIfNull(email);
        var at = email.LastIndexOf('@');
        return at <= 0 ? "•••" : $"{email[0]}•••{email[at..]}";
    }

    public static string StatusText(JiraConfigStatus status) => status switch {
        JiraConfigStatus.Ok => "ok",
        JiraConfigStatus.NeedsTest => "needs_test",
        JiraConfigStatus.AuthFailed => "auth_failed",
        JiraConfigStatus.Unreachable => "unreachable",
        JiraConfigStatus.RotateRequired => "rotate_required",
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };

    public static JiraConfigStatus ParseStatus(string value) => value switch {
        "ok" => JiraConfigStatus.Ok,
        "needs_test" => JiraConfigStatus.NeedsTest,
        "auth_failed" => JiraConfigStatus.AuthFailed,
        "unreachable" => JiraConfigStatus.Unreachable,
        "rotate_required" => JiraConfigStatus.RotateRequired,
        _ => throw new FormatException($"Unknown team_jira_config.status '{value}'."),
    };

    private static JiraTeamConfig ReadConfig(NpgsqlDataReader reader) => new(
        new TeamId(reader.GetGuid(0)),
        reader.GetString(1),
        (byte[])reader.GetValue(2),
        reader.GetString(3),
        reader.GetString(4),
        reader.GetString(5),
        reader.IsDBNull(6) ? null : reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetInt64(7),
        reader.IsDBNull(8) ? null : reader.GetString(8),
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.GetBoolean(10),
        reader.IsDBNull(11) ? null : reader.GetString(11),
        ParseStatus(reader.GetString(12)),
        reader.GetBoolean(13),
        reader.IsDBNull(14) ? null : reader.GetFieldValue<DateTimeOffset>(14));

    // Reads {"email","token"} and writes "email:token" as UTF-8 into destination; -1 when either is missing.
    private static int WriteBasicPair(ReadOnlySpan<byte> json, Span<byte> destination) {
        var reader = new Utf8JsonReader(json);
        int emailLength = -1, tokenLength = -1;
        var scratch = ArrayPool<byte>.Shared.Rent(json.Length);
        try {
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) {
                return -1;
            }

            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName) {
                var isEmail = reader.ValueTextEquals("email"u8);
                var isToken = reader.ValueTextEquals("token"u8);
                if (!reader.Read() || reader.TokenType != JsonTokenType.String) {
                    return -1;
                }
                if (isEmail) {
                    emailLength = reader.CopyString(destination);
                } else if (isToken) {
                    tokenLength = reader.CopyString(scratch);
                }
            }

            if (emailLength <= 0 || tokenLength <= 0 || emailLength + 1 + tokenLength > destination.Length) {
                return -1;
            }

            destination[emailLength] = (byte)':';
            scratch.AsSpan(0, tokenLength).CopyTo(destination[(emailLength + 1)..]);
            return emailLength + 1 + tokenLength;
        } catch (JsonException) {
            return -1;
        } finally {
            CryptographicOperations.ZeroMemory(scratch.AsSpan(0, json.Length));
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }
}
