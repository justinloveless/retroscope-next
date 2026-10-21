using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Retroscope.Core.Security;
using Retroscope.Importer.Map;

namespace Retroscope.Importer.Verify;

/// <summary>
/// Imported bcrypt hashes equal the source hashes, compared by SHA-256 (§h.5). Inside the run each kept user's hash
/// is compared with its own <c>auth.users</c> row, and a digest of every (user, sha256(hash)) pair is recorded as the
/// report fact <c>passwordHashes</c>. The <c>verify</c> verb has no source, so it compares the target with the digest
/// of the last successful run.
/// </summary>
public sealed class PasswordHashCheck : IVerifyCheck {
    public const string Fact = "passwordHashes";

    public string Name => "password-hashes";

    public async Task<IReadOnlyList<string>> RunAsync(VerifyContext context, CancellationToken cancellationToken) {
        var target = new SortedDictionary<Guid, byte[]>();
        foreach (var row in await TargetQueries.RowsAsync(context.Target,
            "select id, password_hash from users where password_hash is not null", cancellationToken)) {
            target[(Guid)row[0]!] = Digest((string)row[1]!);
        }

        if (context.Run is { } run) {
            var failures = await CompareWithSourceAsync(run, target, cancellationToken);
            run.Report.Set(Fact, Summary(target));
            return failures;
        }
        return await CompareWithLastRunAsync(context, target, cancellationToken);
    }

    private static async Task<IReadOnlyList<string>> CompareWithSourceAsync(ImportContext run, SortedDictionary<Guid, byte[]> target, CancellationToken cancellationToken) {
        var expected = new SortedDictionary<Guid, byte[]>();
        foreach (var account in await run.Source.ReadAsync("auth.users", cancellationToken)) {
            var id = account.Uuid("id")!.Value;
            if (run.Principals.IsKeptUser(id) && account.Text("encrypted_password") is { } hash && Bcrypt.IsWellFormed(hash)) {
                expected[id] = Digest(hash);
            }
        }

        var failures = new List<string>();
        foreach (var id in expected.Keys.Union(target.Keys).Order()) {
            var source = expected.GetValueOrDefault(id);
            var imported = target.GetValueOrDefault(id);
            if (source is null) {
                failures.Add($"user {id} has a password hash the source does not have");
            } else if (imported is null) {
                failures.Add($"user {id} lost its password hash");
            } else if (!source.AsSpan().SequenceEqual(imported)) {
                failures.Add($"user {id} has a password hash that differs from the source");
            }
        }
        return TargetQueries.Summarize(failures);
    }

    private static async Task<IReadOnlyList<string>> CompareWithLastRunAsync(VerifyContext context, SortedDictionary<Guid, byte[]> target, CancellationToken cancellationToken) {
        var rows = await TargetQueries.RowsAsync(context.Target, $"""
            select report -> 'facts' -> '{Fact}' ->> 'count', report -> 'facts' -> '{Fact}' ->> 'sha256'
            from archive.import_runs where status = 'succeeded' order by finished_at desc limit 1
            """, cancellationToken);
        if (rows.Count == 0 || rows[0][0] is null) {
            return ["no successful import run recorded password hashes to compare with"];
        }
        var summary = Summary(target);
        return summary["count"]!.ToString() == (string)rows[0][0]! && summary["sha256"]!.GetValue<string>() == (string?)rows[0][1]
            ? []
            : [$"the {summary["count"]} password hashes differ from the {rows[0][0]} the last import wrote"];
    }

    private static byte[] Digest(string hash) => SHA256.HashData(Encoding.UTF8.GetBytes(hash));

    private static JsonObject Summary(SortedDictionary<Guid, byte[]> hashes) {
        var text = new StringBuilder();
        foreach (var (id, digest) in hashes) {
            text.Append(id.ToString("D")).Append(':').Append(Convert.ToHexStringLower(digest)).Append('\n');
        }
        return new JsonObject {
            ["count"] = hashes.Count,
            ["sha256"] = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString()))),
        };
    }
}
