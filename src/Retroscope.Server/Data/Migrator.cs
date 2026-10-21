using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;

namespace Retroscope.Server.Data;

public sealed record MigrationScript(int Version, string Name, byte[] Bytes) {
    public byte[] Sha256 => SHA256.HashData(Bytes);

    public string Sql => Encoding.UTF8.GetString(Bytes);

    // Embedded files are named "NNNN_name.sql".
    public static MigrationScript FromFileName(string fileName, byte[] bytes) {
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var separator = stem.IndexOf('_', StringComparison.Ordinal);
        if (separator <= 0 || !int.TryParse(stem.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out var version)) {
            throw new ArgumentException($"Migration file '{fileName}' is not named NNNN_name.sql.", nameof(fileName));
        }
        return new MigrationScript(version, stem[(separator + 1)..], bytes);
    }
}

// Runs as retroscope_owner. In production the leader lease already serializes migrators (§c.9); the
// transaction-level lock below makes a standalone run (tests, tooling) safe as well.
public sealed class Migrator {
    private const long MigratorLockKey = 1381123154;
    private const string ResourceMarker = ".Migrations.";

    private readonly IReadOnlyList<MigrationScript> _scripts;

    public Migrator(IEnumerable<MigrationScript> scripts) {
        _scripts = [.. scripts.OrderBy(x => x.Version)];
        for (var i = 0; i < _scripts.Count; i++) {
            if (_scripts[i].Version != i + 1) {
                throw new ArgumentException("Migration versions must run 1, 2, 3… without gaps or duplicates.", nameof(scripts));
            }
        }
    }

    public IReadOnlyList<MigrationScript> Scripts => _scripts;

    public static Migrator FromEmbeddedScripts() {
        var assembly = typeof(Migrator).Assembly;
        var scripts = new List<MigrationScript>();
        foreach (var resource in assembly.GetManifestResourceNames()) {
            var marker = resource.IndexOf(ResourceMarker, StringComparison.Ordinal);
            if (marker < 0 || !resource.EndsWith(".sql", StringComparison.Ordinal)) {
                continue;
            }
            using var stream = assembly.GetManifestResourceStream(resource)!;
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            scripts.Add(MigrationScript.FromFileName(resource[(marker + ResourceMarker.Length)..], buffer.ToArray()));
        }
        return new Migrator(scripts);
    }

    /// <summary>
    /// Applies pending migrations, then (re)asserts the app role, its per-role timeouts and its grants,
    /// all in one transaction. Returns the number of migrations applied.
    /// </summary>
    public async Task<int> RunAsync(NpgsqlConnection owner, string appPassword, CancellationToken cancellationToken = default) {
        ArgumentException.ThrowIfNullOrEmpty(appPassword);
        await using var tx = await owner.BeginTransactionAsync(cancellationToken);

        await ExecuteAsync(owner, $"select pg_advisory_xact_lock({MigratorLockKey})", cancellationToken);
        await EnsureRolesAsync(owner, appPassword, cancellationToken);

        var applied = await ReadAppliedAsync(owner, cancellationToken);
        var pending = new List<MigrationScript>();
        foreach (var script in _scripts) {
            if (!applied.TryGetValue(script.Version, out var sha)) {
                pending.Add(script);
            } else if (!sha.AsSpan().SequenceEqual(script.Sha256)) {
                throw new InvalidOperationException(
                    $"Migration {script.Version:D4}_{script.Name} changed after it was applied; refusing to start.");
            }
        }

        foreach (var script in pending) {
            await ExecuteAsync(owner, script.Sql, cancellationToken);
            await using var record = new NpgsqlCommand(
                "insert into schema_migrations (version, name, sha256) values ($1, $2, $3)", owner);
            record.Parameters.Add(new() { Value = script.Version });
            record.Parameters.Add(new() { Value = script.Name });
            record.Parameters.Add(new() { Value = script.Sha256 });
            await record.ExecuteNonQueryAsync(cancellationToken);
        }

        await ExecuteAsync(owner, GrantsSql, cancellationToken);
        await tx.CommitAsync(cancellationToken);
        return pending.Count;
    }

    // Statement timeouts are per role, never server-wide (S2): the owner runs migrations and the
    // one-transaction import, so it must never be cut off. The password travels as a parameter and is
    // quoted by format(), because utility statements cannot take bind parameters.
    private static async Task EnsureRolesAsync(NpgsqlConnection owner, string appPassword, CancellationToken cancellationToken) {
        await using (var password = new NpgsqlCommand("select set_config('retroscope.app_password', $1, true)", owner)) {
            password.Parameters.Add(new() { Value = appPassword });
            await password.ExecuteNonQueryAsync(cancellationToken);
        }
        await ExecuteAsync(owner, $"""
            do $$
            begin
              if not exists (select from pg_roles where rolname = '{Db.AppRole}') then
                create role {Db.AppRole};
              end if;
              execute format('alter role {Db.AppRole} login nosuperuser nocreatedb nocreaterole noreplication nobypassrls password %L',
                             current_setting('retroscope.app_password'));
            end $$;
            alter role {Db.AppRole} set statement_timeout = '5s';
            alter role {Db.AppRole} set idle_in_transaction_session_timeout = '30s';
            alter role current_user set statement_timeout = 0;
            alter role current_user set idle_in_transaction_session_timeout = 0;
            """, cancellationToken);
    }

    private static async Task<Dictionary<int, byte[]>> ReadAppliedAsync(NpgsqlConnection owner, CancellationToken cancellationToken) {
        var applied = new Dictionary<int, byte[]>();
        await using var exists = new NpgsqlCommand("select to_regclass('public.schema_migrations') is not null", owner);
        if (!(bool)(await exists.ExecuteScalarAsync(cancellationToken))!) {
            return applied;
        }
        await using var read = new NpgsqlCommand("select version, sha256 from schema_migrations", owner);
        await using var reader = await read.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            applied.Add(reader.GetInt32(0), reader.GetFieldValue<byte[]>(1));
        }
        return applied;
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken cancellationToken) {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // Re-run on every start, so tables added by later migrations are granted without each file repeating it.
    private const string GrantsSql = $"""
        grant usage on schema public to {Db.AppRole};
        grant select, insert, update, delete on all tables in schema public to {Db.AppRole};
        revoke insert, update, delete on schema_migrations from {Db.AppRole};
        grant execute on function rs_fence(bigint) to {Db.AppRole};
        revoke all on all tables in schema archive from {Db.AppRole};
        revoke all on schema archive from {Db.AppRole};
        """;
}
