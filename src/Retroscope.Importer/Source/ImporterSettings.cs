using Npgsql;
using Retroscope.Core.Security;
using Retroscope.Importer.Map;

namespace Retroscope.Importer.Source;

public sealed class SettingsException(string message) : Exception(message);

/// <summary>
/// The importer's environment (DESIGN §h.1). Connection settings accept Npgsql keyword strings or
/// <c>postgres://</c> URIs (Supabase hands out URIs). The source defaults to <c>SSL Mode=VerifyFull</c>.
/// </summary>
public sealed record ImporterSettings {
    public string? SourceConnectionString { get; init; }
    public string? TargetConnectionString { get; init; }
    public string? SealPublicKeyPem { get; init; }
    public IReadOnlySet<string> AdminEmails { get; init; } = new HashSet<string>(StringComparer.Ordinal);
    public string? AppKeys { get; init; }
    public string? AnonTagKey { get; init; }
    public string? JiraHostAllowlist { get; init; }
    public Uri? StorageUrl { get; init; }

    public static ImporterSettings FromEnvironment(Func<string, string?> get) => new() {
        SourceConnectionString = Optional(get, "IMPORT_SOURCE_PG") is { } source ? WithSourceDefaults(ToKeywords(source)) : null,
        TargetConnectionString = Optional(get, "DATABASE_OWNER_URL") is { } target ? ToKeywords(target) : null,
        SealPublicKeyPem = Optional(get, "IMPORT_SEAL_PUBKEY"),
        AdminEmails = (Optional(get, "IMPORT_ADMIN_EMAILS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(x => PrincipalDirectory.NormalizeEmail(x)!)
            .ToHashSet(StringComparer.Ordinal),
        AppKeys = Optional(get, "APP_KEYS"),
        AnonTagKey = Optional(get, "ANON_TAG_KEY"),
        JiraHostAllowlist = Optional(get, "JIRA_HOST_ALLOWLIST"),
        StorageUrl = Optional(get, "IMPORT_STORAGE_URL") is { } storage ? ParseStorageUrl(storage) : null,
    };

    public string RequireSource() => SourceConnectionString ?? throw new SettingsException("IMPORT_SOURCE_PG is not set.");

    public string RequireTarget() => TargetConnectionString ?? throw new SettingsException("DATABASE_OWNER_URL is not set.");

    public Uri RequireStorageUrl() => StorageUrl ?? throw new SettingsException("IMPORT_STORAGE_URL is not set.");

    public ImportKeys RequireKeys() => new(
        Keyring.Parse(AppKeys ?? throw new SettingsException("APP_KEYS is not set.")),
        AnonTagger.Parse(AnonTagKey ?? throw new SettingsException("ANON_TAG_KEY is not set.")));

    public JiraHostPolicy JiraHosts() => JiraHostPolicy.FromAllowlist(JiraHostAllowlist);

    /// <summary>A null sealer means secrets are stripped (no <c>IMPORT_SEAL_PUBKEY</c>).</summary>
    public Sealer? CreateSealer() => SealPublicKeyPem is null ? null : Sealer.Create(SealPublicKeyPem);

    /// <summary>Refuses a source and target that are the same database on the same host.</summary>
    public void EnsureDistinctDatabases() {
        if (SourceConnectionString is null || TargetConnectionString is null) {
            return;
        }
        var source = new NpgsqlConnectionStringBuilder(SourceConnectionString);
        var target = new NpgsqlConnectionStringBuilder(TargetConnectionString);
        if (string.Equals(source.Host, target.Host, StringComparison.OrdinalIgnoreCase)
            && source.Port == target.Port
            && string.Equals(source.Database ?? source.Username, target.Database ?? target.Username, StringComparison.Ordinal)) {
            throw new SettingsException("IMPORT_SOURCE_PG and DATABASE_OWNER_URL name the same database.");
        }
    }

    /// <summary>Converts <c>postgres[ql]://user:password@host:port/database?sslmode=…</c> to an Npgsql keyword string.</summary>
    public static string ToKeywords(string value) {
        if (!value.StartsWith("postgres://", StringComparison.OrdinalIgnoreCase)
            && !value.StartsWith("postgresql://", StringComparison.OrdinalIgnoreCase)) {
            return new NpgsqlConnectionStringBuilder(value).ConnectionString;
        }

        var uri = new Uri(value);
        var builder = new NpgsqlConnectionStringBuilder {
            Host = uri.Host,
            Port = uri.IsDefaultPort || uri.Port < 0 ? 5432 : uri.Port,
            Database = Uri.UnescapeDataString(uri.AbsolutePath.TrimStart('/')),
        };
        var userInfo = uri.UserInfo.Split(':', 2);
        if (userInfo[0].Length > 0) {
            builder.Username = Uri.UnescapeDataString(userInfo[0]);
        }
        if (userInfo.Length == 2) {
            builder.Password = Uri.UnescapeDataString(userInfo[1]);
        }
        foreach (var pair in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)) {
            var parts = pair.Split('=', 2);
            var key = Uri.UnescapeDataString(parts[0]);
            var setting = parts.Length == 2 ? Uri.UnescapeDataString(parts[1]) : "";
            if (key.Equals("sslmode", StringComparison.OrdinalIgnoreCase)) {
                builder.SslMode = Enum.Parse<SslMode>(setting.Replace("-", "", StringComparison.Ordinal), ignoreCase: true);
            } else {
                builder[key] = setting;
            }
        }
        return builder.ConnectionString;
    }

    private static string WithSourceDefaults(string keywords) {
        var builder = new NpgsqlConnectionStringBuilder(keywords) {
            ApplicationName = TargetDatabase.ApplicationName,
            CommandTimeout = 0,
            Pooling = false,
        };
        if (!keywords.Contains("ssl", StringComparison.OrdinalIgnoreCase)) {
            builder.SslMode = SslMode.VerifyFull;
        }
        return builder.ConnectionString;
    }

    private static Uri ParseStorageUrl(string value) {
        if (!Uri.TryCreate(value.EndsWith('/') ? value : value + "/", UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) {
            throw new SettingsException("IMPORT_STORAGE_URL must be an https URL such as https://<project>.supabase.co.");
        }
        return uri;
    }

    private static string? Optional(Func<string, string?> get, string name) =>
        get(name) is { Length: > 0 } value ? value.Trim() : null;

    // Records print every property; these hold passwords and keys.
    public override string ToString() => nameof(ImporterSettings);
}
