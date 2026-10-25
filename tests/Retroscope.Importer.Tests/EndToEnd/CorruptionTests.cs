using Retroscope.Importer.Tests.Infrastructure;
using static Retroscope.Importer.Tests.EndToEnd.EndToEndImport;

namespace Retroscope.Importer.Tests.EndToEnd;

/// <summary>
/// <c>verify</c> after a clean import, on a copy of the target with one invariant broken (§h.5 hard invariants). Each
/// corruption must be reported by the check that owns the invariant, and by no other check.
/// </summary>
[Trait("Category", "EndToEnd")]
[Collection(ImporterPostgres.Name)]
public sealed class CorruptionTests(ImporterDatabases databases) : IAsyncLifetime {
    public static readonly TheoryData<string, string, string> Corruptions = new() {
        { "no owner", "one-owner", $"update team_members set role = 'admin' where team_id = '{Alpha}' and role = 'owner'" },
        { "card on another board's column", "board-consistency",
            $"update retro_cards set column_id = '{OtherColumn}' where id = '{NamedCard}'" },
        { "vote on the wrong board", "board-consistency", $"update retro_votes set board_id = '{OtherBoard}' where card_id = '{AnonCard}'" },
        { "sort key gap", "poker-rounds", $"update poker_rounds set sort_key = 5 where id = '{VotingRound}'" },
        { "unlocked vote on a revealed round", "poker-rounds",
            $"update poker_votes set locked = false where round_id = '{RevealedRound}' and not abstained" },
        { "body with a control character", "markup", $"update retro_comments set body = 'bad' || chr(1) where id = '{AnonComment}'" },
        { "non-canonical body", "markup", $"update retro_cards set body = '**unclosed' where id = '{NamedCard}'" },
        { "jira box tampered", "jira-config",
            "update team_jira_config set credentials_box = set_byte(credentials_box, 20, get_byte(credentials_box, 20) # 1)" },
        { "password hash changed", "password-hashes", $"update users set password_hash = '$2a$10$' || repeat('A', 53) where id = '{Alice}'" },
        { "email as a display name", "display-names", "update principals set display_name = 'leak@example.com' where kind = 'guest'" },
        { "dangling author", "foreign-keys",
            $"set session_replication_role = replica; update retro_cards set author_id = '00000000-0000-4000-8000-00000000dead' where id = '{NamedCard}'" },
        { "anonymous image names its author", "anonymity", $"update blobs set created_by = '{Alice}' where purpose = 'board_image'" },
        { "action item of an anonymous card names its author", "anonymity",
            $"update team_action_items set created_by = '{Alice}' where id = '{AnonAction}'" },
        { "secret archived in plain text", "sealed-archive",
            """update archive.rows set row = row || '{"jira_api_key": "leaked"}' where source_table = 'public.teams#dropped'""" },
        { "counts do not add up", "counts",
            """update archive.import_runs set report = jsonb_set(report, '{tables,public.teams,imported}', '1')""" },
    };

    // Every test shares one clean import, so none may write to its target: verify runs read-only, and a corruption goes
    // into a clone (CloneTargetAsync). xUnit runs a class's tests one at a time, so no clone races another connection.
    private static readonly Lock _gate = new();
    private static Task<EndToEndImport>? _sharedImport;

    private EndToEndImport? _import;

    public async ValueTask InitializeAsync() {
        lock (_gate) {
            _sharedImport ??= ImportOnceAsync(databases);
        }
        _import = await _sharedImport;
    }

    private static async Task<EndToEndImport> ImportOnceAsync(ImporterDatabases databases) {
        var import = await CreateAsync(databases);
        await import.FetchAndRunAsync();
        return import;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    [Fact]
    public async Task The_clean_import_verifies() => Assert.Empty(await _import!.VerifyAsync());

    [Theory]
    [MemberData(nameof(Corruptions))]
    public async Task Verify_names_the_broken_invariant(string corruption, string check, string sql) {
        var copy = await _import!.CloneTargetAsync();
        await ImporterDatabases.ExecuteAsync(copy, sql);

        var failures = await _import.VerifyAsync(copy);

        Assert.True(failures.Count > 0, $"{corruption} was not caught");
        Assert.All(failures, x => Assert.StartsWith(check + ": ", x, StringComparison.Ordinal));
    }
}
