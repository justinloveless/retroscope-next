using Npgsql;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;

namespace Retroscope.Server.Data.Repos;

/// <param name="SourceKind"><c>card</c> or <c>comment</c>.</param>
/// <param name="Author">Null for anonymous posts and on boards that hide author names ("Anonymous", §d).</param>
/// <param name="Body">The card's or comment's markup as it is now.</param>
public sealed record MentionItem(
    string SourceKind,
    Guid SourceId,
    string BoardCode,
    string BoardTitle,
    AuthorInfo? Author,
    string Body,
    long CreatedAt);

/// <summary>The mentions page (§0.1: a pull-only index, no notifications) and its unread mark.</summary>
public sealed class MentionsRepo(NpgsqlDataSource dataSource) {
    // A board the user can still subscribe to, as Policy.Can(Subscribe) decides it and UsersRepo.UnreadMentionsAsync
    // counts it: board and team not deleted; a member, a link board, or a password board with a current grant (or the
    // quick board's creator).
    private const string ListSql = """
        select m.source_kind, m.source_id, m.created_at, b.code, b.title, coalesce(c.body, x.body),
               coalesce(c.author_id, x.author_id), p.kind,
               case p.kind when 'user' then coalesce(nullif(u.nickname, ''), nullif(u.full_name, ''), 'User') else p.display_name end,
               u.avatar_blob_id, coalesce(c.legacy_author, x.legacy_author), b.show_author_names
        from mentions m
        join retro_boards b on b.id = m.board_id and b.deleted_at is null
        left join teams t on t.id = b.team_id
        left join team_members tm on tm.team_id = b.team_id and tm.user_id = m.user_id
        left join retro_cards c on m.source_kind = 'card' and c.id = m.source_id
        left join retro_comments x on m.source_kind = 'comment' and x.id = m.source_id
        left join principals p on p.id = coalesce(c.author_id, x.author_id)
        left join users u on u.id = coalesce(c.author_id, x.author_id)
        where m.user_id = $1
          and (c.id is not null or x.id is not null)
          and (b.team_id is null or t.deleted_at is null)
          and (tm.user_id is not null
            or b.visibility = 'link'
            or (b.visibility = 'password'
                and ((b.team_id is null and b.created_by = m.user_id)
                  or exists (select 1 from board_access_grants g
                             where g.board_id = b.id and g.principal_id = m.user_id
                               and g.password_epoch = b.password_set_at))))
          and ($2::timestamptz is null or (m.created_at, m.source_id) < ($2, $3))
        order by m.created_at desc, m.source_id desc
        limit $4
        """;

    public async Task<Page<MentionItem>> ListAsync(PrincipalId user, ListCursor? cursor, int limit, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand(ListSql);
        command.Parameters.Add(new() { Value = user.Value });
        ListCursor.AddParameters(command, cursor);
        command.Parameters.Add(new() { Value = limit + 1 });
        var rows = new List<(MentionItem, ListCursor)>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            var id = reader.GetGuid(1);
            var createdAt = reader.GetFieldValue<DateTimeOffset>(2);
            var author = reader.GetBoolean(11) ? ReadAuthor(reader) : null;
            rows.Add((
                new MentionItem(
                    reader.GetString(0),
                    id,
                    reader.GetString(3),
                    reader.GetString(4),
                    author,
                    reader.GetString(5),
                    createdAt.ToUnixTimeMilliseconds()),
                new ListCursor(createdAt, id)));
        }
        return Page.Of(rows, limit);
    }

    public async Task MarkSeenAsync(PrincipalId user, CancellationToken cancellationToken) {
        await using var command = dataSource.CreateCommand("update users set mentions_seen_at = now() where id = $1");
        command.Parameters.Add(new() { Value = user.Value });
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    // The board's own rule (RetroStore): a principal is the author; an imported label without one shows as a guest
    // name; neither (an anonymous tag, or nothing) is anonymous.
    private static AuthorInfo? ReadAuthor(NpgsqlDataReader reader) {
        if (!reader.IsDBNull(6)) {
            var kind = reader.IsDBNull(7) ? "user" : reader.GetString(7);
            return new AuthorInfo(
                reader.IsDBNull(8) ? "User" : reader.GetString(8),
                Guest: kind != "user",
                new PrincipalId(reader.GetGuid(6)),
                reader.IsDBNull(9) ? null : new BlobId(reader.GetGuid(9)));
        }
        return reader.IsDBNull(10) ? null : new AuthorInfo(reader.GetString(10), Guest: true);
    }
}
