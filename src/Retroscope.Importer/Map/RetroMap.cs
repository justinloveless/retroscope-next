using System.Security.Cryptography;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using Retroscope.Core.Ids;
using Retroscope.Core.Markup;
using Retroscope.Core.Security;
using Retroscope.Importer.Archive;
using Retroscope.Importer.Html;
using Retroscope.Importer.Source;

namespace Retroscope.Importer.Map;

/// <summary>
/// Retro boards and everything on them (DESIGN §h.2): <c>retro_boards</c> with their inlined config, columns,
/// cards, comments, votes, readiness, the mentions and board images derived from the bodies, and the guest
/// principals behind guest posts and votes. Users and teams are already in the target (U39's stages run first).
/// </summary>
public sealed class RetroMap : IImportStage {
    public int Order => 400;

    public string Name => "retro";

    public async Task RunAsync(ImportContext context, CancellationToken cancellationToken) {
        var run = new Run(context, await RetroTarget.TeamMembersAsync(context.Target, cancellationToken));
        await run.MapAsync(cancellationToken);
        await run.WriteAsync(cancellationToken);
    }

    private sealed record Board(
        Guid Id,
        string Code,
        Guid? TeamId,
        string Title,
        Guid? CreatedBy,
        string Visibility,
        string? PasswordHash,
        DateTime? PasswordSetAt,
        string Stage,
        BoardConfig Config,
        DateTime? ArchivedAt,
        Guid? ArchivedBy,
        DateTime? DeletedAt,
        DateTime CreatedAt,
        DateTime UpdatedAt);

    private sealed record BoardConfig(
        bool AllowAnonymous = true,
        bool VotingEnabled = true,
        short? MaxVotesPerUser = null,
        bool ShowAuthorNames = true,
        bool StagesEnabled = false,
        bool EnforceReadiness = false,
        bool AllowSelfVotes = true,
        string VoteEmoji = RetroText.DefaultVoteEmoji,
        bool SortChronologically = false,
        int TimerDurationMs = RetroText.DefaultTimerMs,
        bool TimerAlarm = true,
        bool TimerMusic = false);

    private sealed record Column(Guid Id, Guid BoardId, string Title, string Color, int SortKey, bool ActionItems, DateTime CreatedAt);

    private sealed record Card(Guid Id, Guid BoardId, Guid ColumnId, string Body, Authorship Author, DateTime CreatedAt, DateTime UpdatedAt);

    private sealed record Comment(Guid Id, Guid CardId, Guid BoardId, string Body, Authorship Author, DateTime CreatedAt);

    private sealed record Vote(Guid CardId, Guid VoterId, Guid BoardId, DateTime CreatedAt);

    private sealed record Readiness(Guid BoardId, string Stage, Guid PrincipalId, DateTime ReadyAt);

    private sealed record Mention(string SourceKind, Guid SourceId, Guid BoardId, Guid UserId, DateTime CreatedAt);

    private sealed class Run(ImportContext context, IReadOnlyDictionary<Guid, HashSet<Guid>> teamMembers) {
        private const string Boards = "public.retro_boards";
        private const string Configs = "public.retro_board_config";
        private const string Columns = "public.retro_columns";
        private const string Items = "public.retro_items";
        private const string Comments = "public.retro_comments";
        private const string Votes = "public.retro_votes";
        private const string Ready = "public.retro_user_readiness";

        private readonly Dictionary<Guid, Board> _boards = [];
        private readonly Dictionary<Guid, Column> _columns = [];
        private readonly Dictionary<Guid, Card> _cards = [];
        private readonly List<Comment> _comments = [];
        private readonly Dictionary<(Guid Card, Guid Voter), Vote> _votes = [];
        private readonly Dictionary<(Guid Board, string Stage, Guid Principal), Readiness> _readiness = [];
        private readonly List<Mention> _mentions = [];
        private readonly BoardImages _images = new();
        private readonly HashSet<Guid> _guests = [];
        private Dictionary<Guid, string> _guestNames = [];

        public async Task MapAsync(CancellationToken cancellationToken) {
            var items = await context.Source.ReadAsync(Items, cancellationToken);
            var comments = await context.Source.ReadAsync(Comments, cancellationToken);
            _guestNames = GuestNames(items.Concat(comments));

            MapBoards(await context.Source.ReadAsync(Boards, cancellationToken), await context.Source.ReadAsync(Configs, cancellationToken));
            MapColumns(await context.Source.ReadAsync(Columns, cancellationToken));
            MapCards(items);
            MapComments(comments);
            MapVotes(await context.Source.ReadAsync(Votes, cancellationToken));
            MapReadiness(await context.Source.ReadAsync(Ready, cancellationToken));
        }

        public async Task WriteAsync(CancellationToken cancellationToken) {
            var target = context.Target;
            await RetroTarget.InsertGuestsAsync(target, _guests.Order().Select(x => (x, _guestNames.GetValueOrDefault(x, "Guest"))), cancellationToken);
            await WriteBoardsAsync(cancellationToken);
            await _images.WriteAsync(target, context.ImportedAt, cancellationToken);

            await RetroCopy.WriteAsync(target, "retro_columns",
                [("id", NpgsqlDbType.Uuid), ("board_id", NpgsqlDbType.Uuid), ("title", NpgsqlDbType.Text), ("color", NpgsqlDbType.Text),
                 ("sort_key", NpgsqlDbType.Integer), ("is_action_items", NpgsqlDbType.Boolean), ("created_at", NpgsqlDbType.TimestampTz)],
                _columns.Values.OrderBy(x => x.Id),
                x => [x.Id, x.BoardId, x.Title, x.Color, x.SortKey, x.ActionItems, x.CreatedAt],
                cancellationToken);

            await RetroCopy.WriteAsync(target, "retro_cards",
                [("id", NpgsqlDbType.Uuid), ("board_id", NpgsqlDbType.Uuid), ("column_id", NpgsqlDbType.Uuid), ("body", NpgsqlDbType.Text),
                 ("author_id", NpgsqlDbType.Uuid), ("anon_tag", NpgsqlDbType.Bytea), ("legacy_author", NpgsqlDbType.Text),
                 ("created_at", NpgsqlDbType.TimestampTz), ("updated_at", NpgsqlDbType.TimestampTz)],
                _cards.Values.OrderBy(x => x.Id),
                x => [x.Id, x.BoardId, x.ColumnId, x.Body, x.Author.AuthorId, x.Author.AnonTag, x.Author.LegacyAuthor, x.CreatedAt, x.UpdatedAt],
                cancellationToken);

            await RetroCopy.WriteAsync(target, "retro_comments",
                [("id", NpgsqlDbType.Uuid), ("card_id", NpgsqlDbType.Uuid), ("board_id", NpgsqlDbType.Uuid), ("body", NpgsqlDbType.Text),
                 ("author_id", NpgsqlDbType.Uuid), ("anon_tag", NpgsqlDbType.Bytea), ("legacy_author", NpgsqlDbType.Text),
                 ("created_at", NpgsqlDbType.TimestampTz)],
                _comments.OrderBy(x => x.Id),
                x => [x.Id, x.CardId, x.BoardId, x.Body, x.Author.AuthorId, x.Author.AnonTag, x.Author.LegacyAuthor, x.CreatedAt],
                cancellationToken);

            await RetroCopy.WriteAsync(target, "retro_votes",
                [("card_id", NpgsqlDbType.Uuid), ("voter_id", NpgsqlDbType.Uuid), ("board_id", NpgsqlDbType.Uuid), ("created_at", NpgsqlDbType.TimestampTz)],
                _votes.Values.OrderBy(x => x.CardId).ThenBy(x => x.VoterId),
                x => [x.CardId, x.VoterId, x.BoardId, x.CreatedAt],
                cancellationToken);

            await RetroCopy.WriteAsync(target, "retro_readiness",
                [("board_id", NpgsqlDbType.Uuid), ("stage", NpgsqlDbType.Text), ("principal_id", NpgsqlDbType.Uuid), ("ready_at", NpgsqlDbType.TimestampTz)],
                _readiness.Values.OrderBy(x => x.BoardId).ThenBy(x => x.PrincipalId),
                x => [x.BoardId, x.Stage, x.PrincipalId, x.ReadyAt],
                cancellationToken);

            await RetroCopy.WriteAsync(target, "mentions",
                [("source_kind", NpgsqlDbType.Text), ("source_id", NpgsqlDbType.Uuid), ("board_id", NpgsqlDbType.Uuid), ("user_id", NpgsqlDbType.Uuid),
                 ("created_at", NpgsqlDbType.TimestampTz)],
                _mentions.OrderBy(x => x.SourceId).ThenBy(x => x.UserId),
                x => [x.SourceKind, x.SourceId, x.BoardId, x.UserId, x.CreatedAt],
                cancellationToken);
        }

        private Task WriteBoardsAsync(CancellationToken cancellationToken) =>
            RetroCopy.WriteAsync(context.Target, "retro_boards",
                [("id", NpgsqlDbType.Uuid), ("code", NpgsqlDbType.Text), ("team_id", NpgsqlDbType.Uuid), ("title", NpgsqlDbType.Text),
                 ("created_by", NpgsqlDbType.Uuid), ("visibility", NpgsqlDbType.Text), ("password_hash", NpgsqlDbType.Text),
                 ("password_set_at", NpgsqlDbType.TimestampTz), ("stage", NpgsqlDbType.Text), ("allow_anonymous", NpgsqlDbType.Boolean),
                 ("voting_enabled", NpgsqlDbType.Boolean), ("max_votes_per_user", NpgsqlDbType.Smallint), ("show_author_names", NpgsqlDbType.Boolean),
                 ("stages_enabled", NpgsqlDbType.Boolean), ("enforce_readiness", NpgsqlDbType.Boolean), ("allow_self_votes", NpgsqlDbType.Boolean),
                 ("vote_emoji", NpgsqlDbType.Text), ("sort_chronologically", NpgsqlDbType.Boolean), ("timer_state", NpgsqlDbType.Text),
                 ("timer_duration_ms", NpgsqlDbType.Integer), ("timer_alarm", NpgsqlDbType.Boolean), ("timer_music", NpgsqlDbType.Boolean),
                 ("archived_at", NpgsqlDbType.TimestampTz), ("archived_by", NpgsqlDbType.Uuid), ("deleted_at", NpgsqlDbType.TimestampTz),
                 ("last_activity_at", NpgsqlDbType.TimestampTz), ("created_at", NpgsqlDbType.TimestampTz), ("updated_at", NpgsqlDbType.TimestampTz)],
                _boards.Values.OrderBy(x => x.Id),
                x => [
                    x.Id, x.Code, x.TeamId, x.Title, x.CreatedBy, x.Visibility, x.PasswordHash, x.PasswordSetAt, x.Stage,
                    x.Config.AllowAnonymous, x.Config.VotingEnabled, x.Config.MaxVotesPerUser, x.Config.ShowAuthorNames,
                    x.Config.StagesEnabled, x.Config.EnforceReadiness, x.Config.AllowSelfVotes, x.Config.VoteEmoji,
                    x.Config.SortChronologically, "idle", x.Config.TimerDurationMs, x.Config.TimerAlarm, x.Config.TimerMusic,
                    x.ArchivedAt, x.ArchivedBy, x.DeletedAt, x.UpdatedAt, x.CreatedAt, x.UpdatedAt,
                ],
                cancellationToken);

        private void MapBoards(IReadOnlyList<SourceRow> rows, IReadOnlyList<SourceRow> configRows) {
            var configs = MapConfigs(configRows, rows.Select(x => x.Uuid("id")!.Value).ToHashSet());
            var codes = new HashSet<string>(StringComparer.Ordinal);
            var tally = context.Report.Table(Boards);
            foreach (var row in rows) {
                var id = row.Uuid("id")!.Value;
                var createdAt = row.Timestamp("created_at") ?? row.Timestamp("updated_at") ?? context.ImportedAt;
                var updatedAt = row.Timestamp("updated_at") ?? createdAt;
                var teamId = row.Uuid("team_id") is { } team && teamMembers.ContainsKey(team) ? team : (Guid?)null;
                if (row.Uuid("team_id") is not null && teamId is null) {
                    context.Report.Count("retroBoards.teamMissing");
                }
                var createdBy = context.PrincipalRef("retro_boards", "creator_id", row.Uuid("creator_id"));
                var code = BoardCode(id, row.Text("room_id"), codes);
                var (visibility, hash) = Visibility(row, id, code, teamId, createdBy);
                var archived = row.Bool("archived") == true;
                var stage = row.Text("retro_stage") is { } value && RetroText.Stages.Contains(value) ? value : "thinking";

                _boards[id] = new Board(
                    id,
                    code,
                    teamId,
                    RetroText.Cap(row.Text("title"), 200, "Retro"),
                    createdBy,
                    visibility,
                    hash,
                    hash is null ? null : context.ImportedAt,
                    stage,
                    configs.GetValueOrDefault(id) ?? new BoardConfig(),
                    archived ? row.Timestamp("archived_at") ?? updatedAt : null,
                    archived ? context.PrincipalRef("retro_boards", "archived_by", row.Uuid("archived_by")) : null,
                    row.Bool("deleted") == true ? updatedAt : null,
                    createdAt,
                    updatedAt);
                tally.Imported++;
            }
        }

        private Dictionary<Guid, BoardConfig> MapConfigs(IReadOnlyList<SourceRow> rows, HashSet<Guid> boards) {
            var configs = new Dictionary<Guid, BoardConfig>();
            var tally = context.Report.Table(Configs);
            foreach (var row in rows) {
                var boardId = row.Uuid("board_id")!.Value;
                if (!boards.Contains(boardId)) {
                    tally.Skip("boardMissing");
                    continue;
                }
                if (configs.ContainsKey(boardId)) {
                    tally.Skip("duplicate");
                    continue;
                }
                var defaults = new BoardConfig();
                configs[boardId] = new BoardConfig(
                    row.Bool("allow_anonymous") ?? defaults.AllowAnonymous,
                    row.Bool("voting_enabled") ?? defaults.VotingEnabled,
                    row.Number("max_votes_per_user") is { } max and >= 1 ? (short)Math.Min(max, 99) : null,
                    row.Bool("show_author_names") ?? defaults.ShowAuthorNames,
                    row.Bool("retro_stages_enabled") ?? defaults.StagesEnabled,
                    row.Bool("enforce_stage_readiness") ?? defaults.EnforceReadiness,
                    row.Bool("allow_self_votes") ?? defaults.AllowSelfVotes,
                    RetroText.Cap(row.Text("vote_emoji"), 8, RetroText.DefaultVoteEmoji),
                    row.Bool("sort_chronologically") ?? defaults.SortChronologically,
                    RetroText.TimerDuration(row.Number("timer_duration_seconds")),
                    row.Bool("timer_alarm_enabled") ?? defaults.TimerAlarm,
                    row.Bool("timer_music_enabled") ?? defaults.TimerMusic);
                tally.Imported++;
            }
            return configs;
        }

        private string BoardCode(Guid id, string? roomId, HashSet<string> used) {
            var code = roomId is not null && RetroText.IsBoardCode(roomId) && !used.Contains(roomId) ? roomId : null;
            if (code is null) {
                code = used.Contains(id.ToString("D")) ? id.ToString("N") : id.ToString("D");
                context.Report.Count("retroBoards.recoded");
                context.Report.Note("recodedBoards", new JsonObject { ["board"] = id.ToString(), ["roomId"] = roomId, ["code"] = code });
            }
            used.Add(code);
            return code;
        }

        // DESIGN §h.2 visibility (C). Old board passwords are not honoured: private quick boards get a fresh
        // password the operator hands to the creator or an unknowable one.
        private (string Visibility, string? Hash) Visibility(SourceRow row, Guid id, string code, Guid? teamId, Guid? createdBy) {
            if (row.Bool("is_private") != true) {
                return ("link", null);
            }
            if (teamId is not null) {
                return ("team", null);
            }

            var old = row.Text("password_hash");
            if (string.IsNullOrEmpty(old) || RetroText.IsSha256Hex(old)) {
                context.Report.Count("retroBoards.unknowablePasswords");
                context.Report.Note("unknowableBoardPasswords", new JsonObject {
                    ["board"] = id.ToString(),
                    ["code"] = code,
                    ["reason"] = string.IsNullOrEmpty(old) ? "no password" : "sha256 of an orphaned team board",
                });
                return ("password", Bcrypt.Hash(Convert.ToBase64String(RandomNumberGenerator.GetBytes(32))));
            }

            var password = RandomNumberGenerator.GetString(RetroText.PasswordAlphabet, RetroText.RotatedPasswordLength);
            context.Report.Count("retroBoards.rotatedPasswords");
            context.Report.Note("rotatedBoardPasswords", new JsonObject {
                ["board"] = id.ToString(),
                ["code"] = code,
                ["createdBy"] = createdBy?.ToString(),
                ["password"] = context.Archive.Protect(Boards, id.ToString(), "rotated_password", password),
            });
            return ("password", Bcrypt.Hash(password));
        }

        private void MapColumns(IReadOnlyList<SourceRow> rows) {
            var tally = context.Report.Table(Columns);
            var kept = new List<SourceRow>();
            foreach (var row in rows) {
                if (row.Uuid("board_id") is not { } boardId) {
                    tally.Skip("orphan");
                } else if (!_boards.ContainsKey(boardId)) {
                    tally.Skip("boardMissing");
                } else {
                    kept.Add(row);
                }
            }

            foreach (var board in kept.GroupBy(x => x.Uuid("board_id")!.Value)) {
                var ordered = board
                    .OrderBy(x => x.Number("position") ?? 0)
                    .ThenBy(x => x.Number("sort_order") ?? 0)
                    .ThenBy(x => x.Timestamp("created_at") ?? DateTime.MinValue)
                    .ThenBy(x => x.Uuid("id"))
                    .ToList();
                var actionItemsTaken = false;
                for (var sortKey = 0; sortKey < ordered.Count; sortKey++) {
                    var row = ordered[sortKey];
                    var actionItems = row.Bool("is_action_items") == true;
                    if (actionItems && actionItemsTaken) {
                        actionItems = false;
                        context.Report.Count("retroColumns.extraActionItemsFlags");
                    }
                    actionItemsTaken |= actionItems;
                    var id = row.Uuid("id")!.Value;
                    _columns[id] = new Column(
                        id,
                        board.Key,
                        RetroText.Cap(row.Text("title"), 100, "Untitled"),
                        RetroText.Color(row.Text("color")),
                        sortKey,
                        actionItems,
                        row.Timestamp("created_at") ?? _boards[board.Key].CreatedAt);
                    tally.Imported++;
                }
            }
        }

        private void MapCards(IReadOnlyList<SourceRow> rows) {
            var tally = context.Report.Table(Items);
            foreach (var row in rows) {
                if (row.Uuid("column_id") is not { } columnId || !_columns.TryGetValue(columnId, out var column)) {
                    tally.Skip(row.Uuid("board_id") is null && row.Uuid("column_id") is null ? "orphan" : "columnMissing");
                    continue;
                }
                if (row.Uuid("board_id") is { } boardId && boardId != column.BoardId) {
                    context.Report.Count("retroItems.boardFromColumn");
                }

                var id = row.Uuid("id")!.Value;
                var createdAt = row.Timestamp("created_at") ?? context.ImportedAt;
                var author = Author(row, "retro_items", column.BoardId);
                var body = ConvertBody(row, MarkupField.Card, column.BoardId, Items, author, createdAt);
                _cards[id] = new Card(id, column.BoardId, columnId, body.Markup, author, createdAt, row.Timestamp("updated_at") ?? createdAt);
                AddMentions("card", id, column.BoardId, body, createdAt);
                tally.Imported++;
            }
        }

        private void MapComments(IReadOnlyList<SourceRow> rows) {
            var tally = context.Report.Table(Comments);
            foreach (var row in rows) {
                if (!_cards.TryGetValue(row.Uuid("item_id")!.Value, out var card)) {
                    tally.Skip("cardMissing");
                    continue;
                }

                var id = row.Uuid("id")!.Value;
                var createdAt = row.Timestamp("created_at") ?? context.ImportedAt;
                var author = Author(row, "retro_comments", card.BoardId);
                var body = ConvertBody(row, MarkupField.Comment, card.BoardId, Comments, author, createdAt);
                _comments.Add(new Comment(id, card.Id, card.BoardId, body.Markup, author, createdAt));
                AddMentions("comment", id, card.BoardId, body, createdAt);
                tally.Imported++;
            }
        }

        // Votes collapse on (card, voter), keeping the newest; the counter column is never read, so the recount
        // compares each card's imported votes with its source vote rows.
        private void MapVotes(IReadOnlyList<SourceRow> rows) {
            var tally = context.Report.Table(Votes);
            var sourceCounts = new Dictionary<Guid, int>();
            foreach (var row in rows.OrderByDescending(x => x.Timestamp("created_at") ?? DateTime.MinValue).ThenBy(x => x.Uuid("id"))) {
                if (row.Uuid("item_id") is not { } cardId || !_cards.TryGetValue(cardId, out var card)) {
                    tally.Skip("cardMissing");
                    continue;
                }
                sourceCounts[cardId] = sourceCounts.GetValueOrDefault(cardId) + 1;
                if (Principal(row, "retro_votes", "user_id") is not { } voter) {
                    tally.Skip("voterMissing");
                    continue;
                }
                if (!_votes.TryAdd((cardId, voter), new Vote(cardId, voter, card.BoardId, row.Timestamp("created_at") ?? card.CreatedAt))) {
                    tally.Skip("duplicate");
                    continue;
                }
                UseGuest(voter);
                tally.Imported++;
            }

            var imported = _votes.Values.GroupBy(x => x.CardId).ToDictionary(x => x.Key, x => x.Count());
            foreach (var (cardId, source) in sourceCounts.OrderBy(x => x.Key)) {
                var count = imported.GetValueOrDefault(cardId);
                if (count != source) {
                    context.Report.Count("retroVotes.recountDrift");
                    context.Report.Note("voteRecount", new JsonObject { ["card"] = cardId.ToString(), ["source"] = source, ["imported"] = count });
                }
            }
        }

        private void MapReadiness(IReadOnlyList<SourceRow> rows) {
            var tally = context.Report.Table(Ready);
            foreach (var row in rows) {
                if (!_boards.TryGetValue(row.Uuid("board_id")!.Value, out var board)) {
                    tally.Skip("boardMissing");
                    continue;
                }
                if (row.Bool("is_ready") != true || row.Text("current_stage") != board.Stage) {
                    context.Archive.AddRow(row);
                    tally.Archived++;
                    continue;
                }
                if (Principal(row, "retro_user_readiness", "user_id") is not { } principal) {
                    tally.Skip("principalMissing");
                    continue;
                }
                if (!_readiness.TryAdd((board.Id, board.Stage, principal), new Readiness(board.Id, board.Stage, principal, row.Timestamp("updated_at") ?? row.Timestamp("created_at") ?? context.ImportedAt))) {
                    tally.Skip("duplicate");
                    continue;
                }
                UseGuest(principal);
                tally.Imported++;
            }
        }

        private HtmlConversion ConvertBody(SourceRow row, MarkupField field, Guid boardId, string table, Authorship author, DateTime createdAt) {
            var conversion = HtmlToMarkup.Convert(row.Text("text"), new HtmlContext(field, boardId, id => context.Principals.User(id)));
            conversion.Report(context.Report, table, row.PrimaryKey);
            _images.Add(conversion, author.AuthorId, author.Anonymous, createdAt);
            return conversion;
        }

        private void AddMentions(string kind, Guid sourceId, Guid boardId, HtmlConversion body, DateTime createdAt) {
            if (_boards[boardId].TeamId is not { } teamId) {
                return;
            }
            var members = teamMembers[teamId];
            foreach (var user in Mentions.Extract(body.Doc).Where(members.Contains)) {
                _mentions.Add(new Mention(kind, sourceId, boardId, user, createdAt));
            }
        }

        // DESIGN §h.2 authorship. Anonymous posts keep only an anon tag bound to the poster, never the poster; a guest
        // post names a synthetic guest principal; a post with neither keeps its label as legacy_author.
        private Authorship Author(SourceRow row, string table, Guid boardId) {
            var principal = Principal(row, table, "author_id");
            if (RetroAuthorship.IsAnonymous(row)) {
                return new Authorship(null, principal is { } poster ? context.Keys.AnonTagger.Tag(new BoardId(boardId), new PrincipalId(poster)) : null, null, Anonymous: true);
            }
            if (principal is { } author) {
                UseGuest(author);
                return new Authorship(author, null, null, Anonymous: false);
            }
            var label = row.Text("author");
            return new Authorship(null, null, string.IsNullOrWhiteSpace(label) ? null : DisplayNames.Scrub(label), Anonymous: false);
        }

        private Guid? Principal(SourceRow row, string table, string column) =>
            context.PrincipalRef(table, column, row.Uuid(column)) ?? RetroAuthorship.Guest(row.Text("session_id"));

        private void UseGuest(Guid principal) {
            if (context.Principals.Principal(principal) is null) {
                _guests.Add(principal);
            }
        }

        // A guest is named by the latest label it posted under (scrubbed, S27); a guest that only voted is "Guest".
        private Dictionary<Guid, string> GuestNames(IEnumerable<SourceRow> posts) =>
            posts
                .Where(x => !RetroAuthorship.IsAnonymous(x) && context.Principals.Principal(x.Uuid("author_id")) is null)
                .Select(x => (Guest: RetroAuthorship.Guest(x.Text("session_id")), At: x.Timestamp("created_at") ?? DateTime.MinValue, Label: x.Text("author")))
                .Where(x => x.Guest is not null)
                .GroupBy(x => x.Guest!.Value)
                .ToDictionary(x => x.Key, x => DisplayNames.Scrub(x.OrderByDescending(y => y.At).First().Label));
    }
}

internal sealed record Authorship(Guid? AuthorId, byte[]? AnonTag, string? LegacyAuthor, bool Anonymous);

/// <summary>Source authorship rules shared by cards, comments and the action items copied from cards.</summary>
internal static class RetroAuthorship {
    /// <summary>The old client wrote <c>author = 'Anonymous'</c> (also the column default) for anonymous posts.</summary>
    public static bool IsAnonymous(SourceRow row) => string.Equals(row.Text("author")?.Trim(), "Anonymous", StringComparison.Ordinal);

    public static Guid? Guest(string? sessionId) => string.IsNullOrWhiteSpace(sessionId) ? null : SyntheticIds.Guest(sessionId);
}

internal static partial class RetroText {
    public const string DefaultVoteEmoji = "👍";
    public const int DefaultTimerMs = 900_000;
    public const int RotatedPasswordLength = 12;

    // No look-alikes (0/O, 1/l/I), so the password survives being read aloud or retyped.
    public const string PasswordAlphabet = "abcdefghijkmnpqrstuvwxyzABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static readonly HashSet<string> Stages = new(StringComparer.Ordinal) { "thinking", "voting", "discussing", "closed" };

    private static readonly HashSet<string> Palette = new(StringComparer.Ordinal) {
        "green", "red", "blue", "yellow", "purple", "pink", "indigo", "orange", "gray",
    };

    /// <summary>Trimmed and capped at <paramref name="maxCodePoints"/> code points (the §b <c>char_length</c> checks).</summary>
    public static string Cap(string? text, int maxCodePoints, string fallback) {
        var trimmed = text?.Trim() ?? "";
        if (trimmed.Length == 0) {
            return fallback;
        }
        var runes = trimmed.EnumerateRunes().ToList();
        return runes.Count <= maxCodePoints ? trimmed : string.Concat(runes.Take(maxCodePoints).Select(x => x.ToString())).TrimEnd();
    }

    /// <summary>A Tailwind class string such as <c>bg-green-100 border-green-300</c> → its palette colour; unknown → gray.</summary>
    public static string Color(string? classes) {
        var value = classes?.Trim().ToLowerInvariant() ?? "";
        if (Palette.Contains(value)) {
            return value;
        }
        var match = TailwindColor().Match(value);
        return match.Success && Palette.Contains(match.Groups["color"].Value) ? match.Groups["color"].Value : "gray";
    }

    /// <summary>Seconds → milliseconds within the §b range; 0 or null (never set) keeps the default.</summary>
    public static int TimerDuration(int? seconds) =>
        seconds is > 0 ? (int)Math.Clamp(seconds.Value * 1000L, 1000, 7_200_000) : DefaultTimerMs;

    public static bool IsBoardCode(string code) => BoardCode().IsMatch(code);

    public static bool IsSha256Hex(string value) => Sha256Hex().IsMatch(value);

    [GeneratedRegex(@"(?:^|\s)(?:bg|border|text)-(?<color>[a-z]+)-[0-9]{2,3}(?:\s|$)", RegexOptions.CultureInvariant)]
    private static partial Regex TailwindColor();

    [GeneratedRegex(@"^[A-Za-z0-9-]{3,64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex BoardCode();

    [GeneratedRegex(@"^[0-9A-Fa-f]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Hex();
}

/// <summary>Target-side reads and writes shared by the retro stages.</summary>
internal static class RetroTarget {
    /// <summary>Every imported team → its member user ids.</summary>
    public static async Task<IReadOnlyDictionary<Guid, HashSet<Guid>>> TeamMembersAsync(NpgsqlConnection target, CancellationToken cancellationToken) {
        var teams = new Dictionary<Guid, HashSet<Guid>>();
        await using var command = new NpgsqlCommand(
            "select t.id, m.user_id from teams t left join team_members m on m.team_id = t.id", target);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            var team = reader.GetGuid(0);
            if (!teams.TryGetValue(team, out var members)) {
                teams[team] = members = [];
            }
            if (!reader.IsDBNull(1)) {
                members.Add(reader.GetGuid(1));
            }
        }
        return teams;
    }

    public static async Task<HashSet<Guid>> IdsAsync(NpgsqlConnection target, string sql, CancellationToken cancellationToken) {
        var ids = new HashSet<Guid>();
        await using var command = new NpgsqlCommand(sql, target);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken)) {
            ids.Add(reader.GetGuid(0));
        }
        return ids;
    }

    /// <summary>Synthetic guests; another stage (poker) may already have created the same <c>v5(guest:…)</c>.</summary>
    public static async Task InsertGuestsAsync(NpgsqlConnection target, IEnumerable<(Guid Id, string Name)> guests, CancellationToken cancellationToken) {
        var list = guests.ToList();
        await using var command = new NpgsqlCommand("""
            insert into principals (id, kind, display_name)
            select id, 'guest', name from unnest($1::uuid[], $2::text[]) as g(id, name)
            on conflict (id) do nothing
            """, target) {
            Parameters = {
                new() { Value = list.Select(x => x.Id).ToArray() },
                new() { Value = list.Select(x => x.Name).ToArray() },
            },
        };
        await command.ExecuteNonQueryAsync(cancellationToken);
    }
}

/// <summary>Binary COPY of mapped rows into one target table.</summary>
internal static class RetroCopy {
    public static async Task WriteAsync<T>(
        NpgsqlConnection target,
        string table,
        IReadOnlyList<(string Name, NpgsqlDbType Type)> columns,
        IEnumerable<T> rows,
        Func<T, object?[]> values,
        CancellationToken cancellationToken) {
        await using var import = await target.BeginBinaryImportAsync(
            $"copy {table} ({string.Join(", ", columns.Select(x => x.Name))}) from stdin (format binary)", cancellationToken);
        foreach (var row in rows) {
            await import.StartRowAsync(cancellationToken);
            var fields = values(row);
            for (var i = 0; i < columns.Count; i++) {
                if (fields[i] is { } value) {
                    await import.WriteAsync(value, columns[i].Type, cancellationToken);
                } else {
                    await import.WriteNullAsync(cancellationToken);
                }
            }
        }
        await import.CompleteAsync(cancellationToken);
    }
}
