using System.Net.Mail;
using System.Text.Json;
using Npgsql;
using NpgsqlTypes;

// Proves, under Native AOT, the library behaviour the server design depends on (DESIGN §a AOT rules, §12).
var connectionString = Environment.GetEnvironmentVariable("SMOKE_DATABASE_URL");
if (string.IsNullOrEmpty(connectionString)) {
    Console.Error.WriteLine("SMOKE_DATABASE_URL is not set");
    return 2;
}

var checks = new (string Name, Func<NpgsqlDataSource, Task> Run)[] {
    ("unnest(uuid[], int[]) update", Checks.UnnestUpdateAsync),
    ("jsonb round trip", Checks.JsonbRoundTripAsync),
    ("batch in explicit transaction", Checks.BatchInTransactionAsync),
    ("auto-prepare", Checks.AutoPrepareAsync),
    ("bcrypt verify", _ => Checks.BcryptVerify()),
    ("SmtpClient construction", _ => Checks.SmtpClientConstruction()),
};

var builder = new NpgsqlSlimDataSourceBuilder(connectionString);
builder.EnableArrays();
builder.EnableJsonTypes();
builder.ConnectionStringBuilder.MaxAutoPrepare = 50;
builder.ConnectionStringBuilder.AutoPrepareMinUsages = 2;
await using var dataSource = builder.Build();

var failures = 0;
foreach (var (name, run) in checks) {
    try {
        await run(dataSource);
        Console.WriteLine($"ok   {name}");
    } catch (Exception e) {
        failures++;
        Console.WriteLine($"FAIL {name}: {e.GetType().Name}: {e.Message}");
    }
}

return failures == 0 ? 0 : 1;

internal static class Checks {
    // GoTrue-style cost-10 hash of the 6-character password "abc123" (generated with htpasswd -B, prefix $2y$ → $2a$).
    private const string ImportedHash = "$2a$10$0Xbn4A5oJBCQZubbHwLBz.CQP/bZsLlp./C7Ym1ZoVNkQVifQRHLO";

    public static async Task UnnestUpdateAsync(NpgsqlDataSource dataSource) {
        await using var connection = await dataSource.OpenConnectionAsync();
        await Execute(connection, "create temp table smoke_rounds (id uuid primary key, sort_key int not null)");

        var ids = new[] { Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid() };
        await using (var insert = new NpgsqlCommand("insert into smoke_rounds select unnest($1::uuid[]), 0", connection)) {
            insert.Parameters.Add(new NpgsqlParameter { Value = ids });
            await insert.ExecuteNonQueryAsync();
        }

        var keys = new[] { 3, 1, 2 };
        await using (var update = new NpgsqlCommand(
            "update smoke_rounds r set sort_key = u.k from unnest($1::uuid[], $2::int[]) as u(id, k) where r.id = u.id",
            connection)) {
            update.Parameters.Add(new NpgsqlParameter { Value = ids });
            update.Parameters.Add(new NpgsqlParameter { Value = keys });
            Expect(await update.ExecuteNonQueryAsync() == 3, "expected 3 updated rows");
        }

        await using var select = new NpgsqlCommand("select array_agg(id order by sort_key) from smoke_rounds", connection);
        var ordered = (Guid[])(await select.ExecuteScalarAsync())!;
        Expect(ordered.SequenceEqual(new[] { ids[1], ids[2], ids[0] }), "unexpected order after unnest update");
    }

    public static async Task JsonbRoundTripAsync(NpgsqlDataSource dataSource) {
        const string Document = """{"deck":["1","2","3","5"],"autoReveal":true,"note":"ünïcode"}""";
        await using var command = dataSource.CreateCommand("select $1::jsonb");
        command.Parameters.Add(new NpgsqlParameter { Value = Document, NpgsqlDbType = NpgsqlDbType.Jsonb });
        var echoed = (string)(await command.ExecuteScalarAsync())!;

        using var sent = JsonDocument.Parse(Document);
        using var received = JsonDocument.Parse(echoed);
        Expect(JsonElement.DeepEquals(sent.RootElement, received.RootElement), "jsonb document changed in transit");
    }

    public static async Task BatchInTransactionAsync(NpgsqlDataSource dataSource) {
        await using var connection = await dataSource.OpenConnectionAsync();
        await Execute(connection, "create temp table smoke_events (n int not null)");

        await using (var transaction = await connection.BeginTransactionAsync()) {
            await using var batch = new NpgsqlBatch(connection, transaction) {
                BatchCommands = {
                    new NpgsqlBatchCommand("insert into smoke_events values (1)"),
                    new NpgsqlBatchCommand("insert into smoke_events values (2)"),
                    new NpgsqlBatchCommand("select 1/0"),
                },
            };
            try {
                await batch.ExecuteNonQueryAsync();
                throw new InvalidOperationException("a failing statement in the batch did not raise");
            } catch (PostgresException e) when (e.SqlState == PostgresErrorCodes.DivisionByZero) {
                await transaction.RollbackAsync();
            }
        }

        await using var count = new NpgsqlCommand("select count(*) from smoke_events", connection);
        Expect((long)(await count.ExecuteScalarAsync())! == 0, "a failed batch committed part of its work");
    }

    public static async Task AutoPrepareAsync(NpgsqlDataSource dataSource) {
        await using var connection = await dataSource.OpenConnectionAsync();
        for (var i = 0; i < 3; i++) {
            await using var command = new NpgsqlCommand("select $1::int + 1", connection);
            command.Parameters.Add(new NpgsqlParameter { Value = i });
            await command.ExecuteScalarAsync();
        }

        await using var prepared = new NpgsqlCommand(
            "select count(*) from pg_prepared_statements where statement like '%$1::int + 1%'",
            connection);
        Expect((long)(await prepared.ExecuteScalarAsync())! > 0, "hot statement was not auto-prepared");
    }

    public static Task BcryptVerify() {
        Expect(BCrypt.Net.BCrypt.Verify("abc123", ImportedHash), "imported $2a$10$ hash did not verify");
        Expect(!BCrypt.Net.BCrypt.Verify("abc124", ImportedHash), "wrong password verified");

        var fresh = BCrypt.Net.BCrypt.HashPassword("correct horse", workFactor: 10);
        Expect(fresh.StartsWith("$2a$10$", StringComparison.Ordinal), $"unexpected hash prefix {fresh[..7]}");
        Expect(BCrypt.Net.BCrypt.Verify("correct horse", fresh), "fresh hash did not verify");
        return Task.CompletedTask;
    }

    public static Task SmtpClientConstruction() {
        using var client = new SmtpClient("smtp.invalid", 587) {
            EnableSsl = true,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Credentials = new System.Net.NetworkCredential("user", "password"),
        };
        using var message = new MailMessage("from@example.com", "to@example.com", "subject", "body");
        Expect(client.Port == 587 && message.To.Count == 1, "SmtpClient or MailMessage misconfigured");
        return Task.CompletedTask;
    }

    private static async Task Execute(NpgsqlConnection connection, string sql) {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync();
    }

    private static void Expect(bool condition, string message) {
        if (!condition) {
            throw new InvalidOperationException(message);
        }
    }
}
