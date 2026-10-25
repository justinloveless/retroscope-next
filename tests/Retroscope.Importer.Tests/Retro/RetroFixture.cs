using Npgsql;
using Retroscope.Importer.Archive;
using Retroscope.Importer.Map;
using Retroscope.Importer.Source;
using Retroscope.Importer.Tests.Infrastructure;

namespace Retroscope.Importer.Tests.Retro;

/// <summary>
/// A source with every retro case in it, imported by the retro stages alone. Users, teams and memberships are
/// written to the target by a seed stage standing in for the identity stages (U39), so these tests do not depend on
/// their mapping.
/// </summary>
public sealed class RetroFixture {
    public const string Alice = "a11ce000-0000-4000-8000-000000000001";
    public const string AliceOld = "a11ce000-0000-4000-8000-000000000002";
    public const string Bob = "b0b00000-0000-4000-8000-000000000001";
    public const string Carol = "ca401000-0000-4000-8000-000000000001";
    public const string AnonAuth = "a0a0a0a0-0000-4000-8000-000000000001";
    public const string Team = "7ea70000-0000-4000-8000-000000000001";
    public const string GoneTeam = "7ea70000-0000-4000-8000-0000000000ff";

    public const string PublicBoard = "b0a40000-0000-4000-8000-000000000001";
    public const string TeamBoard = "b0a40000-0000-4000-8000-000000000002";
    public const string PlainBoard = "b0a40000-0000-4000-8000-000000000003";
    public const string ShaBoard = "b0a40000-0000-4000-8000-000000000004";
    public const string DemoBoard = "b0a40000-0000-4000-8000-000000000005";
    public const string OldBoard = "b0a40000-0000-4000-8000-000000000006";

    public const string GoodColumn = "c0100000-0000-4000-8000-000000000001";
    public const string BadColumn = "c0100000-0000-4000-8000-000000000002";
    public const string ActionColumn = "c0100000-0000-4000-8000-000000000003";
    public const string SecondActionColumn = "c0100000-0000-4000-8000-000000000004";
    public const string OrphanColumn = "c0100000-0000-4000-8000-000000000005";
    public const string QuickColumn = "c0100000-0000-4000-8000-000000000006";

    public const string AnonCard = "17e00000-0000-4000-8000-000000000001";
    public const string NamedCard = "17e00000-0000-4000-8000-000000000002";
    public const string GuestCard = "17e00000-0000-4000-8000-000000000003";
    public const string LegacyCard = "17e00000-0000-4000-8000-000000000004";
    public const string OrphanCard = "17e00000-0000-4000-8000-000000000005";
    public const string BoardlessCard = "17e00000-0000-4000-8000-000000000006";
    public const string XssCard = "17e00000-0000-4000-8000-000000000007";
    public const string AnonGuestCard = "17e00000-0000-4000-8000-000000000008";
    public const string QuickCard = "17e00000-0000-4000-8000-000000000009";

    public const string AnonComment = "c0aa0000-0000-4000-8000-000000000001";
    public const string GuestComment = "c0aa0000-0000-4000-8000-000000000002";
    public const string OrphanComment = "c0aa0000-0000-4000-8000-000000000003";
    public const string QuickComment = "c0aa0000-0000-4000-8000-000000000004";

    public const string AnonAction = "ac710000-0000-4000-8000-000000000001";
    public const string DoneAction = "ac710000-0000-4000-8000-000000000002";
    public const string GoneTeamAction = "ac710000-0000-4000-8000-000000000003";
    public const string LooseAction = "ac710000-0000-4000-8000-000000000004";

    public const string GuestSession = "sess-guest";
    public const string VoterSession = "sess-voter";

    /// <summary>A guest who only ever posted on a quick board, which is not imported.</summary>
    public const string QuickSession = "sess-quick";

    public static readonly byte[] PngA = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xAA];
    public static readonly byte[] PngB = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0xBB];

    public static readonly string[] RetroTables = [
        "public.retro_boards", "public.retro_board_config", "public.retro_columns", "public.retro_items",
        "public.retro_comments", "public.retro_votes", "public.retro_user_readiness", "public.team_action_items",
    ];

    private RetroFixture(ImportHarness harness) => Harness = harness;

    public ImportHarness Harness { get; }

    public ImportContext? Context { get; private set; }

    public static string ImageA => $"<img src=\"data:image/png;base64,{Convert.ToBase64String(PngA)}\">";

    public static string ImageB => $"<img src=\"data:image/png;base64,{Convert.ToBase64String(PngB)}\">";

    public static async Task<RetroFixture> CreateAsync(ImporterDatabases databases) {
        var fixture = new RetroFixture(await ImportHarness.CreateAsync(databases));
        await fixture.Harness.SourceSqlAsync(SourceSql);
        return fixture;
    }

    public ImportRunner Runner(string? sealPublicKeyPem = null) =>
        Harness.Runner(
            stages: [
                new DelegateStage(100, SeedIdentityAsync),
                new RetroMap(),
                new ActionItemsMap(),
                new ArchiveStage(),
                new DelegateStage(999, (context, _) => {
                    Context = context;
                    return Task.CompletedTask;
                }),
            ],
            sealPublicKeyPem: sealPublicKeyPem);

    public Task<T> ScalarAsync<T>(string sql) => Harness.TargetScalarAsync<T>(sql);

    private static async Task SeedIdentityAsync(ImportContext context, CancellationToken cancellationToken) {
        await using var command = new NpgsqlCommand($"""
            insert into principals (id, kind, display_name) values
              ('{Alice}', 'user', null), ('{Bob}', 'user', null), ('{Carol}', 'user', null), ('{AnonAuth}', 'guest', 'Guest');
            insert into users (id, email) values
              ('{Alice}', 'alice@example.com'), ('{Bob}', 'bob@example.com'), ('{Carol}', 'carol@example.com');
            insert into teams (id, name) values ('{Team}', 'Team');
            insert into team_members (team_id, user_id, role) values ('{Team}', '{Alice}', 'owner'), ('{Team}', '{Bob}', 'member');
            """, context.Target);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string SourceSql => $"""
        set session_replication_role = replica;
        -- The dump has this index, but §h.2 does not trust the live database to (columns were edited without a transaction).
        drop index public.idx_retro_columns_action_items_per_board;

        insert into auth.users (id, email, last_sign_in_at, created_at, is_anonymous) values
          ('{Alice}', 'alice@example.com', '2026-01-02', '2025-01-01', false),
          ('{AliceOld}', 'ALICE@example.com', '2025-06-01', '2024-01-01', false),
          ('{Bob}', 'bob@example.com', '2026-01-01', '2025-01-01', false),
          ('{Carol}', 'carol@example.com', '2026-01-01', '2025-01-01', false),
          ('{AnonAuth}', null, null, '2025-01-01', true);
        insert into public.teams (id, name) values ('{Team}', 'Team');
        insert into public.team_members (team_id, user_id, role) values ('{Team}', '{Alice}', 'owner'), ('{Team}', '{Bob}', 'member');

        insert into public.retro_boards (id, room_id, title, is_private, password_hash, creator_id, team_id, archived, archived_at, deleted, retro_stage, created_at, updated_at) values
          ('{PublicBoard}', 'PUB123', '  Public retro  ', false, null, '{Alice}', '{Team}', false, null, false, 'thinking', '2026-01-01', '2026-01-02'),
          ('{TeamBoard}', 'TEAM01', 'Team retro', true, '{new string('a', 64)}', null, '{Team}', false, null, false, 'voting', '2026-01-01', '2026-01-03'),
          ('{PlainBoard}', 'PLAIN1', 'Plain', true, 'hunter2', '{Bob}', null, false, null, false, null, '2026-01-01', '2026-01-01'),
          ('{ShaBoard}', 'SHA001', 'Orphaned team board', true, '{new string('b', 64)}', null, null, false, null, false, 'closed', '2026-01-01', '2026-01-01'),
          ('{DemoBoard}', 'DEMO01', 'Demo', true, 'demo123', null, '{GoneTeam}', false, null, false, 'thinking', '2026-01-01', '2026-01-01'),
          ('{OldBoard}', 'bad code!', '', false, null, null, '{Team}', true, null, true, 'discussing', '2025-01-01', '2025-02-01');

        insert into public.retro_board_config (board_id, allow_anonymous, voting_enabled, max_votes_per_user, show_author_names,
            retro_stages_enabled, enforce_stage_readiness, allow_self_votes, vote_emoji, sort_chronologically, timer_duration_seconds,
            timer_alarm_enabled, timer_music_enabled) values
          ('{PublicBoard}', false, true, 3, false, true, true, false, '🎉🎉🎉🎉🎉🎉🎉🎉🎉🎉', true, 300, false, true),
          ('{OldBoard}', true, true, 0, true, false, false, true, '', false, 0, true, false),
          ('{PlainBoard}', true, true, 0, true, false, false, true, '👍', false, 600, true, false);

        insert into public.retro_columns (id, board_id, title, color, position, sort_order, is_action_items, created_at) values
          ('{GoodColumn}', '{TeamBoard}', 'Good', 'bg-green-100 border-green-300', 1, 0, false, '2026-01-01'),
          ('{BadColumn}', '{TeamBoard}', 'Bad', 'bg-red-100 border-red-300', 0, 0, false, '2026-01-01'),
          ('{ActionColumn}', '{TeamBoard}', 'Actions', 'bg-yellow-100', 2, 0, true, '2026-01-01'),
          ('{SecondActionColumn}', '{TeamBoard}', '', 'bg-teal-100', 3, 0, true, '2026-01-01'),
          ('{OrphanColumn}', null, 'Lost', 'bg-blue-100', 0, 0, false, '2026-01-01'),
          ('{QuickColumn}', '{PlainBoard}', 'Quick', 'bg-blue-100', 0, 0, false, '2026-01-01');

        insert into public.retro_items (id, board_id, column_id, text, author, author_id, session_id, votes, created_at, updated_at) values
          ('{AnonCard}', '{TeamBoard}', '{ActionColumn}', '<p>secret {ImageA} cc [[mention:{Bob}:Bob]]</p>', 'Anonymous', '{Alice}', 'sess-alice', 0, '2026-01-01 10:00', '2026-01-01 10:00'),
          ('{NamedCard}', '{TeamBoard}', '{GoodColumn}', '<p><strong>named</strong> {ImageB} [[mention:{Carol}:Carol]]</p>', 'Alice', '{AliceOld}', null, 7, '2026-01-01 10:01', '2026-01-01 10:01'),
          ('{GuestCard}', '{TeamBoard}', '{GoodColumn}', '<p>from a guest</p>', 'Sneaky Penguin', null, '{GuestSession}', 0, '2026-01-01 10:02', '2026-01-01 10:02'),
          ('{LegacyCard}', '{TeamBoard}', '{BadColumn}', 'old plain text', 'Old Name', null, null, 0, '2026-01-01 10:03', '2026-01-01 10:03'),
          ('{OrphanCard}', null, null, '<p>lost</p>', 'x', null, null, 0, '2026-01-01 10:04', '2026-01-01 10:04'),
          ('{BoardlessCard}', null, '{BadColumn}', '<p>board from column</p>', 'Bob', '{Bob}', null, 0, '2026-01-01 10:05', '2026-01-01 10:05'),
          ('{XssCard}', '{TeamBoard}', '{BadColumn}', '<p>&lt;div&gt;pasted&lt;/div&gt;<script>alert(1)</script><a href="javascript:alert(1)">x</a></p>', 'Bob', '{Bob}', null, 0, '2026-01-01 10:06', '2026-01-01 10:06'),
          ('{AnonGuestCard}', '{TeamBoard}', '{BadColumn}', '<p>anonymous guest</p>', 'Anonymous', null, '{GuestSession}', 0, '2026-01-01 10:07', '2026-01-01 10:07'),
          ('{QuickCard}', '{PlainBoard}', '{QuickColumn}', '<p>quick {ImageB}</p>', 'Quick Guest', null, '{QuickSession}', 1, '2026-01-01 10:08', '2026-01-01 10:08');

        insert into public.retro_comments (id, item_id, author, author_id, text, session_id, created_at, updated_at) values
          ('{AnonComment}', '{NamedCard}', 'Anonymous', '{Bob}', '<p>anon comment {ImageA}</p>', 'sess-bob', '2026-01-01 11:00', '2026-01-01 11:00'),
          ('{GuestComment}', '{GuestCard}', 'guest@example.com', null, '<p>later name</p>', '{GuestSession}', '2026-01-01 11:01', '2026-01-01 11:01'),
          ('{OrphanComment}', '{OrphanCard}', 'x', null, '<p>lost</p>', null, '2026-01-01 11:02', '2026-01-01 11:02'),
          ('{QuickComment}', '{QuickCard}', 'Quick Guest', null, '<p>quick reply</p>', '{QuickSession}', '2026-01-01 11:03', '2026-01-01 11:03');

        insert into public.retro_votes (id, item_id, user_id, session_id, board_id, created_at) values
          (gen_random_uuid(), '{NamedCard}', '{Alice}', null, '{TeamBoard}', '2026-01-01 12:00'),
          (gen_random_uuid(), '{NamedCard}', '{AliceOld}', null, null, '2026-01-01 12:01'),
          (gen_random_uuid(), '{NamedCard}', null, '{VoterSession}', null, '2026-01-01 12:02'),
          (gen_random_uuid(), '{GuestCard}', '{Bob}', null, '{PublicBoard}', '2026-01-01 12:03'),
          (gen_random_uuid(), '{OrphanCard}', '{Bob}', null, null, '2026-01-01 12:04'),
          (gen_random_uuid(), '{QuickCard}', null, '{QuickSession}', '{PlainBoard}', '2026-01-01 12:05');

        insert into public.retro_user_readiness (board_id, user_id, session_id, current_stage, is_ready, created_at, updated_at) values
          ('{TeamBoard}', '{Alice}', null, 'voting', true, '2026-01-01', '2026-01-01 13:00'),
          ('{TeamBoard}', '{Bob}', null, 'thinking', true, '2026-01-01', '2026-01-01'),
          ('{TeamBoard}', null, '{GuestSession}', 'voting', false, '2026-01-01', '2026-01-01'),
          ('{PlainBoard}', null, '{QuickSession}', 'thinking', true, '2026-01-01', '2026-01-01');

        insert into public.team_action_items (id, team_id, text, source_board_id, source_item_id, created_at, created_by, done, done_at, done_by, assigned_to) values
          ('{AnonAction}', '{Team}', '<p>secret {ImageA}</p>', '{TeamBoard}', '{AnonCard}', '2026-01-02', '{Alice}', false, '2026-01-03', '{Bob}', '{Bob}'),
          ('{DoneAction}', '{Team}', '<p>done</p>', '{TeamBoard}', '{NamedCard}', '2026-01-02', '{Bob}', true, null, '{AliceOld}', '{Carol}'),
          ('{GoneTeamAction}', '{GoneTeam}', '<p>gone</p>', null, null, '2026-01-02', '{Bob}', false, null, null, null),
          ('{LooseAction}', '{Team}', '<p>no board {ImageB}</p>', '{GoneTeam}', '{OrphanCard}', '2026-01-02', '{AnonAuth}', false, null, null, '{AnonAuth}');
        """;
}
