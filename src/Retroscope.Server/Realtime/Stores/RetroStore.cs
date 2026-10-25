using System.Runtime.CompilerServices;
using Npgsql;
using NpgsqlTypes;
using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;
using Retroscope.Core.Room;
using Retroscope.Core.Team;
using Retroscope.Server.Data;
using Retroscope.Server.Data.Repos;
using Retroscope.Server.Realtime.Actors;

namespace Retroscope.Server.Realtime.Stores;

/// <summary>
/// Loads a board and persists its events (§c.2: the board actor is the single writer of the retro tables, and
/// creates team action items in place). Anonymous posts store only their tag: no principal reaches
/// <c>author_id</c>, <c>blobs.created_by</c> or <c>team_action_items.created_by</c> (S6).
/// </summary>
public sealed class RetroStore(
    NpgsqlDataSource dataSource,
    Leader leader,
    CursorRepo cursors,
    ITeamNotices? notices,
    ILogger<RetroStore> logger)
    : SqlRoomStore<RetroState, RetroEvent>(dataSource, leader, cursors), IRoomStore<RetroState, RetroEvent> {
    private const string DisplayName = "coalesce(nullif(u.nickname, ''), nullif(u.full_name, ''), 'User')";

    private const string AuthorColumns = $"""
        x.author_id, p.kind,
        case p.kind when 'user' then {DisplayName} else p.display_name end,
        u.avatar_blob_id, x.anon_tag, x.legacy_author
        """;

    private const string AuthorJoins = """
        left join principals p on p.id = x.author_id
        left join users u on u.id = x.author_id
        """;

    // Every statement takes the board id as $1. A deleted board, a board of a deleted team, or a board without a team
    // (a leftover quick board, UX18) loads as missing.
    private static readonly string[] LoadStatements = [
        """
        select b.code, b.team_id, b.title, b.archived_at is not null, b.stage, b.focus_card_id,
               b.allow_anonymous, b.voting_enabled, b.show_author_names, b.stages_enabled, b.enforce_readiness,
               b.allow_self_votes, b.vote_emoji, b.sort_chronologically, b.max_votes_per_user,
               b.timer_state, b.timer_duration_ms, b.timer_alarm, b.timer_music, b.timer_ends_at, b.timer_remaining_ms,
               t.max_endorsements_per_board
        from retro_boards b
        join teams t on t.id = b.team_id
        where b.id = $1 and b.deleted_at is null and t.deleted_at is null
        """,
        "select id, title, color, sort_key, is_action_items, version from retro_columns where board_id = $1 order by sort_key",
        $"select x.id, x.column_id, x.body, x.version, x.created_at, {AuthorColumns} from retro_cards x {AuthorJoins} where x.board_id = $1",
        $"select x.id, x.card_id, x.body, x.created_at, {AuthorColumns} from retro_comments x {AuthorJoins} where x.board_id = $1",
        "select card_id, voter_id from retro_votes where board_id = $1",
        """
        select r.principal_id from retro_readiness r
        join retro_boards b on b.id = r.board_id and b.stage = r.stage
        where r.board_id = $1
        """,
        "select id, type_id, from_user_id, to_user_id, created_at from endorsements where board_id = $1",
        """
        select e.id, e.name, e.emoji, e.archived_at is not null, e.description from endorsement_types e
        join retro_boards b on b.team_id = e.team_id
        where b.id = $1
        order by e.sort_key, e.id
        """,
        $"""
        select m.user_id, {DisplayName}, m.role, u.avatar_blob_id from team_members m
        join retro_boards b on b.team_id = m.team_id
        join users u on u.id = m.user_id
        where b.id = $1
        """,
        "select a.source_card_id, a.id from team_action_items a join retro_cards c on c.id = a.source_card_id where c.board_id = $1",
    ];

    // Which action item each source card of a loaded board feeds, so comment counts reach the team actor without a
    // query after the commit. Items are created in place by the board (CardAdded) or imported, so the load and the
    // board's own events keep it complete.
    private readonly ConditionalWeakTable<RetroState, Dictionary<CardId, ActionItemId>> _itemsBySource = [];

    public override async Task<RetroState?> LoadAsync(RoomKey room, DateTimeOffset now, CancellationToken cancellationToken) {
        await using var connection = await DataSource.OpenConnectionAsync(cancellationToken);
        await using var batch = new NpgsqlBatch(connection);
        foreach (var sql in LoadStatements) {
            batch.BatchCommands.Add(new NpgsqlBatchCommand(sql) { Parameters = { Id(room.Id) } });
        }
        await using var reader = await batch.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken)) {
            return null;
        }
        var state = ReadBoard(reader, new BoardId(room.Id));
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            state.Columns.Add(new Column {
                Id = new ColumnId(reader.GetGuid(0)),
                Title = reader.GetString(1),
                Color = RetroSql.ParseColor(reader.GetString(2)),
                SortKey = reader.GetInt32(3),
                ActionItems = reader.GetBoolean(4),
                Version = reader.GetInt32(5),
            });
        }
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            var card = new Card {
                Id = new CardId(reader.GetGuid(0)),
                ColumnId = new ColumnId(reader.GetGuid(1)),
                Body = reader.GetString(2),
                Version = reader.GetInt32(3),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(4),
                Authorship = ReadAuthorship(reader, 5),
            };
            state.Cards[card.Id] = card;
        }
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            var comment = new Comment {
                Id = new CommentId(reader.GetGuid(0)),
                CardId = new CardId(reader.GetGuid(1)),
                Body = reader.GetString(2),
                CreatedAt = reader.GetFieldValue<DateTimeOffset>(3),
                Authorship = ReadAuthorship(reader, 4),
            };
            state.Comments[comment.Id] = comment;
        }
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            state.Cards[new CardId(reader.GetGuid(0))].Voters.Add(new PrincipalId(reader.GetGuid(1)));
        }
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            state.ReadyThisStage.Add(new PrincipalId(reader.GetGuid(0)));
        }
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            var endorsement = new EndorsementInfo(
                new EndorsementId(reader.GetGuid(0)),
                new EndorsementTypeId(reader.GetGuid(1)),
                new PrincipalId(reader.GetGuid(2)),
                new PrincipalId(reader.GetGuid(3)),
                reader.GetFieldValue<DateTimeOffset>(4).ToUnixTimeMilliseconds());
            state.Endorsements[endorsement.Id] = endorsement;
        }
        await reader.NextResultAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            state.EndorsementTypes.Add(new EndorsementTypeInfo(
                new EndorsementTypeId(reader.GetGuid(0)),
                reader.GetString(1),
                reader.GetString(2),
                reader.GetBoolean(3),
                reader.IsDBNull(4) ? null : reader.GetString(4)));
        }
        await reader.NextResultAsync(cancellationToken);
        var members = new List<MemberInfo>();
        while (await reader.ReadAsync(cancellationToken)) {
            members.Add(new MemberInfo(
                new PrincipalId(reader.GetGuid(0)),
                reader.GetString(1),
                RetroSql.ParseRole(reader.GetString(2)),
                reader.IsDBNull(3) ? null : new BlobId(reader.GetGuid(3))));
        }
        state.Members.AddRange(TeamProjection.Roster(members));
        await reader.NextResultAsync(cancellationToken);
        var items = new Dictionary<CardId, ActionItemId>();
        while (await reader.ReadAsync(cancellationToken)) {
            items[new CardId(reader.GetGuid(0))] = new ActionItemId(reader.GetGuid(1));
        }
        _itemsBySource.AddOrUpdate(state, items);
        return state;
    }

    private static RetroState ReadBoard(NpgsqlDataReader reader, BoardId board) {
        var timerState = RetroSql.ParseTimerState(reader.GetString(15));
        var state = new RetroState {
            BoardId = board,
            Code = reader.GetString(0),
            TeamId = new TeamId(reader.GetGuid(1)),
            Title = reader.GetString(2),
            Archived = reader.GetBoolean(3),
            Stage = RetroSql.ParseStage(reader.GetString(4)),
            Focus = reader.IsDBNull(5) ? null : new CardId(reader.GetGuid(5)),
            Config = new RetroConfig(
                AllowAnonymous: reader.GetBoolean(6),
                VotingEnabled: reader.GetBoolean(7),
                ShowAuthorNames: reader.GetBoolean(8),
                StagesEnabled: reader.GetBoolean(9),
                EnforceReadiness: reader.GetBoolean(10),
                AllowSelfVotes: reader.GetBoolean(11),
                VoteEmoji: reader.GetString(12),
                SortChronologically: reader.GetBoolean(13),
                MaxVotes: reader.IsDBNull(14) ? null : reader.GetInt16(14)),
            Timer = new TimerValue(
                timerState,
                reader.GetInt32(16),
                reader.GetBoolean(17),
                reader.GetBoolean(18),
                EndsAt: reader.IsDBNull(19) ? null : reader.GetFieldValue<DateTimeOffset>(19),
                RemainingMs: reader.IsDBNull(20) ? null : reader.GetInt32(20)),
        };
        if (!reader.IsDBNull(21)) {
            state.MaxEndorsements = reader.GetInt16(21);
        }
        return state;
    }

    /// <summary>
    /// A principal's post names them (users by nickname or full name, never the email); an anonymous post keeps only
    /// its tag; an imported label without a principal shows as a guest name.
    /// </summary>
    private static Authorship ReadAuthorship(NpgsqlDataReader reader, int first) {
        if (!reader.IsDBNull(first)) {
            var kind = reader.IsDBNull(first + 1) ? "user" : reader.GetString(first + 1);
            var author = new AuthorInfo(
                reader.IsDBNull(first + 2) ? "User" : reader.GetString(first + 2),
                Guest: kind != "user",
                new PrincipalId(reader.GetGuid(first)),
                reader.IsDBNull(first + 3) ? null : new BlobId(reader.GetGuid(first + 3)));
            return new Authorship(author, null);
        }
        if (!reader.IsDBNull(first + 4)) {
            return new Authorship(null, reader.GetFieldValue<byte[]>(first + 4));
        }
        return reader.IsDBNull(first + 5)
            ? new Authorship(null, null)
            : new Authorship(new AuthorInfo(reader.GetString(first + 5), Guest: true), null);
    }

    protected override IEnumerable<NpgsqlBatchCommand> Plan(RoomKey room, RetroState state, IReadOnlyList<RetroEvent> events) {
        var board = state.BoardId.Value;
        foreach (var evt in events) {
            foreach (var command in PlanEvent(board, state, evt)) {
                yield return command;
            }
        }
        yield return Sql(
            "update retro_boards set last_activity_at = now() where id = $1 and last_activity_at < now() - interval '1 minute'",
            Id(board));
    }

    private static IEnumerable<NpgsqlBatchCommand> PlanEvent(Guid board, RetroState state, RetroEvent evt) {
        switch (evt) {
            case CardAdded e:
                yield return Sql(
                    """
                    insert into retro_cards (id, board_id, column_id, body, author_id, anon_tag, created_at, updated_at)
                    values ($1, $2, $3, $4, $5, $6, $7, $7)
                    """,
                    Id(e.CardId.Value),
                    Id(board),
                    Id(e.ColumnId.Value),
                    Text(e.Body),
                    Id(e.Authorship.Author?.Pid?.Value),
                    Bytes(e.Authorship.AnonTag),
                    Time(e.CreatedAt));
                foreach (var command in Attach(board, e.Images, e.Authorship.Anonymous)) {
                    yield return command;
                }
                yield return InsertMentions("card", e.CardId.Value, board, e.Mentions, e.CreatedAt);
                if (e.ActionItem is { } item) {
                    yield return InsertActionItem(state, item, e.CreatedAt);
                }
                break;
            case CardEdited e:
                yield return Sql(
                    "update retro_cards set body = $3, version = $4, updated_at = now() where id = $1 and board_id = $2",
                    Id(e.CardId.Value),
                    Id(board),
                    Text(e.Body),
                    Int(e.Version));
                foreach (var command in Attach(board, e.Images, e.Anonymous)) {
                    yield return command;
                }
                foreach (var command in ReplaceMentions("card", e.CardId.Value, board, e.Mentions)) {
                    yield return command;
                }
                break;
            case CardDeleted e:
                yield return DeleteMentions([e.CardId.Value], e.Comments.Select(x => x.Value));
                yield return Sql("delete from retro_cards where id = $1 and board_id = $2", Id(e.CardId.Value), Id(board));
                break;
            case VoteSet { On: true } e:
                yield return Sql(
                    "insert into retro_votes (card_id, voter_id, board_id) values ($1, $2, $3) on conflict do nothing",
                    Id(e.CardId.Value),
                    Id(e.Voter.Value),
                    Id(board));
                break;
            case VoteSet e:
                yield return Sql("delete from retro_votes where card_id = $1 and voter_id = $2", Id(e.CardId.Value), Id(e.Voter.Value));
                break;
            case CommentAdded e:
                yield return Sql(
                    """
                    insert into retro_comments (id, card_id, board_id, body, author_id, anon_tag, created_at)
                    values ($1, $2, $3, $4, $5, $6, $7)
                    """,
                    Id(e.CommentId.Value),
                    Id(e.CardId.Value),
                    Id(board),
                    Text(e.Body),
                    Id(e.Authorship.Author?.Pid?.Value),
                    Bytes(e.Authorship.AnonTag),
                    Time(e.CreatedAt));
                foreach (var command in Attach(board, e.Images, e.Authorship.Anonymous)) {
                    yield return command;
                }
                yield return InsertMentions("comment", e.CommentId.Value, board, e.Mentions, e.CreatedAt);
                break;
            case CommentDeleted e:
                yield return DeleteMentions([], [e.CommentId.Value]);
                yield return Sql("delete from retro_comments where id = $1 and board_id = $2", Id(e.CommentId.Value), Id(board));
                break;
            case ColumnAdded e:
                yield return Sql(
                    "insert into retro_columns (id, board_id, title, color, sort_key) values ($1, $2, $3, $4, $5)",
                    Id(e.ColumnId.Value),
                    Id(board),
                    Text(e.Title),
                    Text(RetroSql.Name(e.Color)),
                    Int(e.SortKey));
                break;
            case ColumnUpdated e:
                yield return Sql(
                    "update retro_columns set title = $3, color = $4, version = $5 where id = $1 and board_id = $2",
                    Id(e.ColumnId.Value),
                    Id(board),
                    Text(e.Title),
                    Text(RetroSql.Name(e.Color)),
                    Int(e.Version));
                break;
            case ColumnDeleted e:
                yield return DeleteMentions(e.Cards.Select(x => x.Value), e.Comments.Select(x => x.Value));
                yield return Sql("delete from retro_columns where id = $1 and board_id = $2", Id(e.ColumnId.Value), Id(board));
                break;
            case ColumnsReordered e:
                // Dense keys 0..n-1; retro_columns_sort_uq is deferred, so swapped keys only have to be unique at COMMIT.
                yield return Sql(
                    """
                    update retro_columns c set sort_key = o.key
                    from unnest($2::uuid[], $3::int[]) as o(id, key)
                    where c.id = o.id and c.board_id = $1
                    """,
                    Id(board),
                    Ids(e.Order.Select(x => x.Value)),
                    Ints(Enumerable.Range(0, e.Order.Count)));
                break;
            case ActionItemsColumnSet e:
                // retro_columns_one_action_uq is not deferrable: clear the old flag before setting the new one.
                yield return Sql("update retro_columns set is_action_items = false where board_id = $1 and is_action_items", Id(board));
                if (e.ColumnId is { } column) {
                    yield return Sql(
                        "update retro_columns set is_action_items = true where id = $1 and board_id = $2",
                        Id(column.Value),
                        Id(board));
                }
                break;
            case BoardRenamed e:
                yield return Sql("update retro_boards set title = $2, updated_at = now() where id = $1", Id(board), Text(e.Title));
                break;
            case ConfigChanged e:
                yield return Sql(
                    """
                    update retro_boards set allow_anonymous = $2, voting_enabled = $3, show_author_names = $4,
                      stages_enabled = $5, enforce_readiness = $6, allow_self_votes = $7, vote_emoji = $8,
                      sort_chronologically = $9, max_votes_per_user = $10, updated_at = now()
                    where id = $1
                    """,
                    Id(board),
                    Bool(e.Config.AllowAnonymous),
                    Bool(e.Config.VotingEnabled),
                    Bool(e.Config.ShowAuthorNames),
                    Bool(e.Config.StagesEnabled),
                    Bool(e.Config.EnforceReadiness),
                    Bool(e.Config.AllowSelfVotes),
                    Text(e.Config.VoteEmoji),
                    Bool(e.Config.SortChronologically),
                    SmallInt(e.Config.MaxVotes));
                break;
            case StageChanged e:
                yield return Sql("update retro_boards set stage = $2, updated_at = now() where id = $1", Id(board), Text(RetroSql.Name(e.To)));
                yield return Sql("delete from retro_readiness where board_id = $1 and stage = $2", Id(board), Text(RetroSql.Name(e.From)));
                break;
            case ReadySet { Ready: true } e:
                yield return Sql(
                    "insert into retro_readiness (board_id, stage, principal_id) values ($1, $2, $3) on conflict do nothing",
                    Id(board),
                    Text(RetroSql.Name(e.Stage)),
                    Id(e.Principal.Value));
                break;
            case ReadySet e:
                yield return Sql(
                    "delete from retro_readiness where board_id = $1 and stage = $2 and principal_id = $3",
                    Id(board),
                    Text(RetroSql.Name(e.Stage)),
                    Id(e.Principal.Value));
                break;
            case FocusChanged e:
                yield return Sql("update retro_boards set focus_card_id = $2 where id = $1", Id(board), Id(e.CardId?.Value));
                break;
            case TimerChanged e:
                yield return Sql(
                    """
                    update retro_boards set timer_state = $2, timer_duration_ms = $3, timer_alarm = $4, timer_music = $5,
                      timer_ends_at = $6, timer_remaining_ms = $7
                    where id = $1
                    """,
                    Id(board),
                    Text(RetroSql.Name(e.Timer.State)),
                    Int(e.Timer.DurationMs),
                    Bool(e.Timer.Alarm),
                    Bool(e.Timer.Music),
                    Time(e.Timer.EndsAt),
                    Int(e.Timer.RemainingMs));
                break;
            case EndorsementAdded e:
                yield return Sql(
                    """
                    insert into endorsements (id, board_id, team_id, type_id, from_user_id, to_user_id, created_at)
                    values ($1, $2, $3, $4, $5, $6, $7)
                    """,
                    Id(e.Endorsement.Id.Value),
                    Id(board),
                    Id(state.TeamId.Value),
                    Id(e.Endorsement.TypeId.Value),
                    Id(e.Endorsement.From.Value),
                    Id(e.Endorsement.To.Value),
                    Time(DateTimeOffset.FromUnixTimeMilliseconds(e.Endorsement.CreatedAt)));
                break;
            case EndorsementRemoved e:
                yield return Sql("delete from endorsements where id = $1 and board_id = $2", Id(e.EndorsementId.Value), Id(board));
                break;
            case ArchivedChanged e:
                yield return Sql(
                    """
                    update retro_boards set archived_at = case when $2 then $3 end, archived_by = case when $2 then $4 end,
                      updated_at = now()
                    where id = $1
                    """,
                    Id(board),
                    Bool(e.Archived),
                    Time(e.At),
                    Id(e.By?.Value));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(evt), evt.GetType().Name, "This retro event is not persisted by the board.");
        }
    }

    /// <summary>
    /// Marks the referenced board images attached, and ownerless for an anonymous post (S6, S7). The count check
    /// fails the whole transaction (division by zero) when an id is missing, on another board or not a board
    /// image: the decider cannot see <c>blobs</c>, so this statement is where a foreign reference is refused.
    /// </summary>
    private static IEnumerable<NpgsqlBatchCommand> Attach(Guid board, IReadOnlyList<BlobId> images, bool anonymous) {
        if (images.Count == 0) {
            yield break;
        }
        yield return Sql(
            """
            with attached as (
              update blobs set attached_at = coalesce(attached_at, now()), created_by = case when $3 then null else created_by end
              where id = any($2) and board_id = $1 and purpose = 'board_image'
              returning 1
            )
            select 1 / (count(*) = cardinality($2))::int from attached
            """,
            Id(board),
            Ids(images.Select(x => x.Value)),
            Bool(anonymous));
    }

    private static NpgsqlBatchCommand InsertMentions(string kind, Guid source, Guid board, IReadOnlyList<PrincipalId> users, DateTimeOffset? at) {
        return Sql(
            """
            insert into mentions (source_kind, source_id, board_id, user_id, created_at)
            select $1, $2, $3, u, coalesce($5, now()) from unnest($4::uuid[]) as u
            on conflict do nothing
            """,
            Text(kind),
            Id(source),
            Id(board),
            Ids(users.Select(x => x.Value)),
            Time(at));
    }

    /// <summary>Mentions that survive an edit keep their original time.</summary>
    private static IEnumerable<NpgsqlBatchCommand> ReplaceMentions(string kind, Guid source, Guid board, IReadOnlyList<PrincipalId> users) {
        yield return Sql(
            "delete from mentions where source_kind = $1 and source_id = $2 and user_id <> all($3)",
            Text(kind),
            Id(source),
            Ids(users.Select(x => x.Value)));
        yield return InsertMentions(kind, source, board, users, null);
    }

    private static NpgsqlBatchCommand DeleteMentions(IEnumerable<Guid> cards, IEnumerable<Guid> comments) {
        return Sql(
            """
            delete from mentions
            where (source_kind = 'card' and source_id = any($1)) or (source_kind = 'comment' and source_id = any($2))
            """,
            Ids(cards),
            Ids(comments));
    }

    /// <summary>Create in place (§c.2): nobody else can be writing a row that does not exist yet.</summary>
    private static NpgsqlBatchCommand InsertActionItem(RetroState state, ActionItemInfo item, DateTimeOffset at) {
        return Sql(
            """
            insert into team_action_items (id, team_id, text, source_board_id, source_card_id, source_version, created_by, created_at)
            values ($1, $2, $3, $4, $5, $6, $7, $8)
            """,
            Id(item.Id.Value),
            Id(state.TeamId.Value),
            Text(item.Text),
            Id(item.SourceBoardId?.Value),
            Id(item.SourceCardId?.Value),
            Int(item.SourceVersion),
            Id(item.CreatedBy?.Value),
            Time(at));
    }

    /// <summary>
    /// The interface is implemented again so the team actor hears about action items only after the board's
    /// transaction committed (§c.2). A notice that cannot be built is logged, never surfaced: the commit stands, and
    /// the team actor repairs copies when it loads.
    /// </summary>
    async Task IRoomStore<RetroState, RetroEvent>.CommitAsync(
        RoomKey room,
        RetroState state,
        IReadOnlyList<RetroEvent> events,
        CursorAdvance? cursor,
        CancellationToken cancellationToken) {
        await CommitAsync(room, state, events, cursor, cancellationToken);
        if (notices is null) {
            return;
        }
        try {
            NotifyTeam(notices, state.TeamId, state, events);
        } catch (Exception e) {
            RetroStoreLog.NoticeFailed(logger, e, room);
        }
    }

    private void NotifyTeam(ITeamNotices notices, TeamId team, RetroState before, IReadOnlyList<RetroEvent> events) {
        var items = _itemsBySource.GetValue(before, static _ => []);
        var commentCounts = new Dictionary<CardId, int>();
        void CountComment(CardId card, int delta) {
            if (!commentCounts.TryGetValue(card, out var count)) {
                count = before.Comments.Values.Count(x => x.CardId == card);
            }
            commentCounts[card] = count + delta;
        }
        foreach (var evt in events) {
            switch (evt) {
                case CardAdded { ActionItem: { } item } e:
                    items[e.CardId] = item.Id;
                    notices.ActionItemCreated(team, item);
                    break;
                case CardEdited e:
                    notices.ActionItemTextChanged(team, e.CardId, e.Body, e.Version);
                    break;
                case CardDeleted e:
                    notices.SourceCardDeleted(team, e.CardId);
                    commentCounts.Remove(e.CardId);
                    items.Remove(e.CardId);
                    break;
                case ColumnDeleted e:
                    foreach (var card in e.Cards) {
                        notices.SourceCardDeleted(team, card);
                        commentCounts.Remove(card);
                        items.Remove(card);
                    }
                    break;
                case CommentAdded e:
                    CountComment(e.CardId, +1);
                    break;
                case CommentDeleted e:
                    CountComment(e.CardId, -1);
                    break;
                case BoardRenamed or StageChanged or ArchivedChanged:
                    notices.BoardsChanged(team, before.BoardId);
                    break;
            }
        }
        foreach (var (card, count) in commentCounts) {
            if (items.TryGetValue(card, out var item)) {
                notices.ActionItemCommentsChanged(team, item, count);
            }
        }
    }

    private static NpgsqlBatchCommand Sql(string sql, params NpgsqlParameter[] parameters) {
        var command = new NpgsqlBatchCommand(sql);
        command.Parameters.AddRange(parameters);
        return command;
    }

    private static NpgsqlParameter Id(Guid? value) => Nullable(value, NpgsqlDbType.Uuid);

    private static NpgsqlParameter<Guid[]> Ids(IEnumerable<Guid> values) {
        return new NpgsqlParameter<Guid[]> { TypedValue = values.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Uuid };
    }

    private static NpgsqlParameter<int[]> Ints(IEnumerable<int> values) {
        return new NpgsqlParameter<int[]> { TypedValue = values.ToArray(), NpgsqlDbType = NpgsqlDbType.Array | NpgsqlDbType.Integer };
    }

    private static NpgsqlParameter<string> Text(string value) => new NpgsqlParameter<string> { TypedValue = value };

    private static NpgsqlParameter<bool> Bool(bool value) => new NpgsqlParameter<bool> { TypedValue = value };

    private static NpgsqlParameter Int(int? value) => Nullable(value, NpgsqlDbType.Integer);

    private static NpgsqlParameter SmallInt(int? value) => Nullable(value is { } v ? (short)v : null, NpgsqlDbType.Smallint);

    private static NpgsqlParameter Time(DateTimeOffset? value) => Nullable(value, NpgsqlDbType.TimestampTz);

    private static NpgsqlParameter Bytes(byte[]? value) => Nullable(value, NpgsqlDbType.Bytea);

    private static NpgsqlParameter Nullable(object? value, NpgsqlDbType type) {
        return new NpgsqlParameter { Value = value ?? DBNull.Value, NpgsqlDbType = type };
    }
}

/// <summary>The text values of the retro enums in §b, which match their wire names.</summary>
internal static class RetroSql {
    public static string Name(RetroStage stage) => stage switch {
        RetroStage.Thinking => "thinking",
        RetroStage.Voting => "voting",
        RetroStage.Discussing => "discussing",
        RetroStage.Closed => "closed",
        _ => throw new ArgumentOutOfRangeException(nameof(stage)),
    };

    public static RetroStage ParseStage(string value) => value switch {
        "thinking" => RetroStage.Thinking,
        "voting" => RetroStage.Voting,
        "discussing" => RetroStage.Discussing,
        "closed" => RetroStage.Closed,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown stage."),
    };

    public static string Name(TimerState state) => state switch {
        TimerState.Idle => "idle",
        TimerState.Running => "running",
        TimerState.Paused => "paused",
        _ => throw new ArgumentOutOfRangeException(nameof(state)),
    };

    public static TimerState ParseTimerState(string value) => value switch {
        "idle" => TimerState.Idle,
        "running" => TimerState.Running,
        "paused" => TimerState.Paused,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown timer state."),
    };

    public static TeamRole ParseRole(string value) => value switch {
        "owner" => TeamRole.Owner,
        "admin" => TeamRole.Admin,
        "member" => TeamRole.Member,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown role."),
    };

    public static string Name(ColumnColor color) => color switch {
        ColumnColor.Green => "green",
        ColumnColor.Red => "red",
        ColumnColor.Blue => "blue",
        ColumnColor.Yellow => "yellow",
        ColumnColor.Purple => "purple",
        ColumnColor.Pink => "pink",
        ColumnColor.Indigo => "indigo",
        ColumnColor.Orange => "orange",
        ColumnColor.Gray => "gray",
        _ => throw new ArgumentOutOfRangeException(nameof(color)),
    };

    public static ColumnColor ParseColor(string value) => value switch {
        "green" => ColumnColor.Green,
        "red" => ColumnColor.Red,
        "blue" => ColumnColor.Blue,
        "yellow" => ColumnColor.Yellow,
        "purple" => ColumnColor.Purple,
        "pink" => ColumnColor.Pink,
        "indigo" => ColumnColor.Indigo,
        "orange" => ColumnColor.Orange,
        "gray" => ColumnColor.Gray,
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown column colour."),
    };
}

internal static partial class RetroStoreLog {
    [LoggerMessage(Level = LogLevel.Warning, Message = "Board {Room} committed, but its team notices could not be sent")]
    public static partial void NoticeFailed(ILogger logger, Exception exception, RoomKey room);
}
