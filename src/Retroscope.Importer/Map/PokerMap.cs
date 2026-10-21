using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Npgsql;
using NpgsqlTypes;
using Retroscope.Core.Poker;
using Retroscope.Importer.Archive;
using Retroscope.Importer.Source;

namespace Retroscope.Importer.Map;

/// <summary>
/// <c>poker_sessions</c> and <c>poker_session_rounds</c> → <c>poker_sessions</c>, <c>poker_observers</c>,
/// <c>poker_rounds</c>, <c>poker_votes</c> and the synthetic guest and Slack principals behind them (DESIGN §h.2).
/// Ticket titles and parent summaries are omitted columns, so they are never read. Spotlight state is ephemeral and
/// starts empty; Jira points were never recorded by the old app, so <c>jira_points*</c> stay null.
/// </summary>
public sealed partial class PokerMap : IImportStage {
    public const string SessionsTable = "public.poker_sessions";
    public const string RoundsTable = "public.poker_session_rounds";
    public const string SlackDisplayName = "Slack user";

    public int Order => 300;

    public string Name => "poker";

    public async Task RunAsync(ImportContext context, CancellationToken cancellationToken) {
        var report = context.Report;
        var teams = await TeamIdsAsync(context.Target, cancellationToken);
        var sessions = await context.Source.ReadAsync(SessionsTable, cancellationToken);
        var rounds = (await context.Source.ReadAsync(RoundsTable, cancellationToken)).ToLookup(x => x.Uuid("session_id")!.Value);
        var voters = new Voters(context.Principals);
        var codes = new SessionCodes();
        var load = new Load();

        foreach (var row in sessions.OrderBy(x => x.Timestamp("created_at")).ThenBy(x => x.PrimaryKey, StringComparer.Ordinal)) {
            var id = row.Uuid("id")!.Value;
            var teamId = row.Uuid("team_id");
            if (teamId is { } team && !teams.Contains(team)) {
                report.Table(SessionsTable).Skip("team_missing");
                SkipOrphans(report, rounds[id].Count());
                continue;
            }

            var observers = (row.Get<Guid[]>("observer_ids") ?? []).Select(voters.Observer).ToHashSet();
            var mapped = MapRounds(rounds[id], observers, voters, report);
            var byNumber = mapped.ToDictionary(x => x.Number);
            var current = row.Number("current_round_number") is { } number && byNumber.TryGetValue(number, out var pointed)
                ? pointed
                : mapped.MaxBy(x => x.Number);
            if (current is not null && current.Number != row.Number("current_round_number")) {
                report.Count("poker.pointerRepaired");
            }

            load.Sessions.Add(new SessionRow(
                id,
                codes.Assign(id, row.Text("room_id"), report),
                teamId,
                row.Bool("presence_enabled") ?? true,
                current?.Id,
                mapped.Count == 0 ? 1 : mapped.Max(x => x.Number) + 1,
                row.Timestamp("last_activity_at")!.Value,
                row.Timestamp("created_at")!.Value));
            load.Observers.AddRange(observers.Order().Select(x => (id, x)));
            load.Rounds.AddRange(mapped);
            report.Table(SessionsTable).Imported++;
        }

        var known = sessions.Select(x => x.Uuid("id")!.Value).ToHashSet();
        SkipOrphans(report, rounds.Where(x => !known.Contains(x.Key)).Sum(x => x.Count()));
        codes.ReportCollisions(report);

        var referenced = load.Observers.Select(x => x.Principal).Concat(load.Rounds.SelectMany(x => x.Votes).Select(x => x.Principal));
        load.Principals.AddRange(voters.Synthetic(referenced));
        report.Count("poker.guestPrincipals", load.Principals.Count(x => x.Kind == "guest"));
        report.Count("poker.slackPrincipals", load.Principals.Count(x => x.Kind == "external"));
        report.Count("poker.votes", load.Rounds.Sum(x => x.Votes.Count));
        await load.WriteAsync(context.Target, cancellationToken);
    }

    private static void SkipOrphans(ImportReport report, int count) {
        if (count > 0) {
            report.Table(RoundsTable).Skip("orphan", count);
        }
    }

    private static List<RoundRow> MapRounds(IEnumerable<SourceRow> rows, IReadOnlySet<Guid> observers, Voters voters, ImportReport report) {
        var kept = new List<SourceRow>();
        foreach (var row in rows) {
            if (row.Bool("is_pending_round") == true) {
                report.Table(RoundsTable).Skip("pending");
            } else {
                kept.Add(row);
            }
        }

        var numbers = Renumber(kept.Select(x => (x.Uuid("id")!.Value, x.Number("round_number")!.Value, x.Timestamp("created_at")!.Value)));
        foreach (var row in kept) {
            var id = row.Uuid("id")!.Value;
            if (numbers[id] != row.Number("round_number")) {
                report.Note("pokerRenumberedRounds", new JsonObject {
                    ["session"] = row.Uuid("session_id")!.Value.ToString(),
                    ["round"] = id.ToString(),
                    ["from"] = row.Number("round_number"),
                    ["to"] = numbers[id],
                });
            }
        }

        var sortKeys = kept
            .OrderBy(x => x.Number("sort_order") ?? int.MaxValue)
            .ThenBy(x => numbers[x.Uuid("id")!.Value])
            .Select((x, index) => (x.Uuid("id")!.Value, index))
            .ToDictionary();

        var mapped = new List<RoundRow>();
        foreach (var row in kept.OrderBy(x => x.Timestamp("created_at")).ThenBy(x => x.PrimaryKey, StringComparer.Ordinal)) {
            var id = row.Uuid("id")!.Value;
            mapped.Add(MapRound(row, numbers[id], sortKeys[id], observers, voters, report));
            report.Table(RoundsTable).Imported++;
        }
        return [.. mapped.OrderBy(x => x.SortKey)];
    }

    private static RoundRow MapRound(SourceRow row, int number, int sortKey, IReadOnlySet<Guid> observers, Voters voters, ImportReport report) {
        var id = row.Uuid("id")!.Value;
        var createdAt = row.Timestamp("created_at")!.Value;
        var completedAt = row.Timestamp("completed_at") ?? createdAt;
        var isActive = row.Bool("is_active") ?? true;
        var average = row.Get<decimal?>("average_points");
        if (average is null) {
            report.Count("poker.nullAveragePoints");
            report.Note("pokerNullAveragePoints", id.ToString());
        }
        var selections = Selection.Parse(row.Text("selections"), report);
        var revealed = IsRevealed(row.Text("game_state"), isActive, average, selections);

        var votes = new List<(Guid Principal, string Key, VoteValue Vote)>();
        foreach (var selection in selections) {
            var principal = voters.Resolve(selection.Key, selection.Name, createdAt);
            if (ToVote(selection, revealed) is not { } vote) {
                report.Count($"poker.droppedSelections.{(selection.Locked || selection.SlackShape ? "offDeck" : "unlocked")}");
            } else if (!revealed && observers.Contains(principal)) {
                report.Count("poker.droppedSelections.observer");
            } else {
                if (selection.High is not null && !vote.Abstained && vote.High is null) {
                    report.Count("poker.droppedHighPoints");
                }
                votes.Add((principal, selection.Key, vote));
            }
        }

        var imported = new List<VoteRow>();
        foreach (var group in votes.GroupBy(x => x.Principal)) {
            if (group.Count() > 1) {
                report.Count("poker.mergedVoteCollisions", group.Count() - 1);
            }
            var winner = group
                .OrderBy(x => Guid.TryParseExact(x.Key, "D", out var key) && key == x.Principal ? 0 : 1)
                .ThenBy(x => x.Key, StringComparer.Ordinal)
                .First();
            imported.Add(new VoteRow(id, winner.Principal, winner.Vote, completedAt));
        }
        imported.Sort((a, b) => a.Principal.CompareTo(b.Principal));

        short? winnerPoints = null;
        if (revealed) {
            winnerPoints = (short)Scoring.Winner(imported
                .Where(x => !x.Vote.Abstained)
                .Select(x => ((int)x.Vote.Points!.Value, (int?)x.Vote.High)));
            if (average is not null && average != winnerPoints) {
                report.Count("poker.winnerMismatches");
                report.Note("pokerWinnerMismatches", new JsonObject {
                    ["round"] = id.ToString(),
                    ["averagePoints"] = average,
                    ["winner"] = winnerPoints,
                });
            }
        }

        return new RoundRow(
            id,
            row.Uuid("session_id")!.Value,
            number,
            sortKey,
            TicketKey(row.Text("ticket_number"), "poker.invalidTicketKeys", report),
            TicketKey(row.Text("ticket_parent_key"), "poker.invalidParentKeys", report),
            revealed,
            isActive,
            row.Bool("auto_reveal_enabled") ?? true,
            winnerPoints,
            revealed ? completedAt : null,
            createdAt,
            imported);
    }

    /// <summary>
    /// <c>Playing</c> is revealed. A <c>Selection</c> round that is no longer active was shown as revealed by the old
    /// app when it had a winner or everyone abstained (legacy rows), so it imports as revealed too.
    /// </summary>
    internal static bool IsRevealed(string? gameState, bool isActive, decimal? averagePoints, IReadOnlyList<Selection> selections) =>
        gameState == "Playing" || (!isActive && (averagePoints > 0 || (selections.Count > 0 && selections.All(x => x.Points == -1m))));

    /// <summary>
    /// Duplicate <c>(session, round_number)</c>: the latest <c>created_at</c> keeps the number and the others, plus
    /// any round numbered below 1, are renumbered above the maximum in creation order.
    /// </summary>
    internal static IReadOnlyDictionary<Guid, int> Renumber(IEnumerable<(Guid Id, int Number, DateTime CreatedAt)> rounds) {
        var all = rounds.ToList();
        var numbers = all
            .Where(x => x.Number >= 1)
            .GroupBy(x => x.Number)
            .Select(g => g.OrderByDescending(x => x.CreatedAt).ThenByDescending(x => x.Id.ToString(), StringComparer.Ordinal).First())
            .ToDictionary(x => x.Id, x => x.Number);
        var next = numbers.Count == 0 ? 1 : numbers.Values.Max() + 1;
        foreach (var round in all.Where(x => !numbers.ContainsKey(x.Id)).OrderBy(x => x.CreatedAt).ThenBy(x => x.Id.ToString(), StringComparer.Ordinal)) {
            numbers[round.Id] = next++;
        }
        return numbers;
    }

    /// <summary>
    /// Revealed rounds keep every entry: <c>-1</c>, unlocked and off-deck entries are abstentions. Voting rounds keep
    /// only locked deck values and locked abstentions; the old client's default <c>{1, unlocked}</c> and placeholder
    /// <c>0</c> rows were synthetic. Slack's shape has no lock flag and counts as locked. The "between" upper end is
    /// kept only when it is the next deck value.
    /// </summary>
    internal static VoteValue? ToVote(Selection selection, bool revealed) {
        var locked = selection.Locked || selection.SlackShape;
        if (locked && DeckValue(selection.Points) is { } points) {
            return new VoteValue(points, DeckValue(selection.High) is { } high && Deck.Next(points) == high ? high : null);
        }
        return revealed || (locked && selection.Points == -1m) ? VoteValue.Abstain : null;
    }

    private static short? DeckValue(decimal? value) =>
        value is { } number && number == decimal.Truncate(number) && number is >= 1 and <= 21 && Deck.Contains((short)number)
            ? (short)number
            : null;

    private static string? TicketKey(string? value, string counter, ImportReport report) {
        if (string.IsNullOrWhiteSpace(value) || value.Trim().StartsWith("round:", StringComparison.Ordinal)) {
            return null;
        }
        if (TicketKeys.TryNormalize(value, prefix: null, out var key)) {
            return key;
        }
        report.Count(counter);
        return null;
    }

    private static async Task<HashSet<Guid>> TeamIdsAsync(NpgsqlConnection target, CancellationToken cancellationToken) {
        await using var command = new NpgsqlCommand("select id from teams", target);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        var teams = new HashSet<Guid>();
        while (await reader.ReadAsync(cancellationToken)) {
            teams.Add(reader.GetGuid(0));
        }
        return teams;
    }

    /// <summary>One entry of <c>selections</c>, in either the web client's or the Slack bot's shape.</summary>
    internal sealed record Selection(string Key, decimal? Points, decimal? High, bool Locked, bool SlackShape, string? Name) {
        /// <summary>
        /// Web client: <c>{points, betweenHighPoints?, locked, name}</c>. Slack bot: <c>{points, display_name}</c>
        /// with no lock flag. Entries that are not objects are counted and dropped.
        /// </summary>
        public static IReadOnlyList<Selection> Parse(string? json, ImportReport report) {
            using var document = TryParse(string.IsNullOrEmpty(json) ? "{}" : json);
            if (document?.RootElement.ValueKind != JsonValueKind.Object) {
                report.Count("poker.droppedSelections.malformed");
                return [];
            }

            var selections = new List<Selection>();
            foreach (var entry in document.RootElement.EnumerateObject()) {
                var value = entry.Value;
                if (value.ValueKind != JsonValueKind.Object) {
                    report.Count("poker.droppedSelections.malformed");
                    continue;
                }
                var slack = value.TryGetProperty("display_name", out _) && !value.TryGetProperty("locked", out _);
                selections.Add(new Selection(
                    entry.Name,
                    Number(value, "points"),
                    Number(value, "betweenHighPoints"),
                    value.TryGetProperty("locked", out var locked) && locked.ValueKind == JsonValueKind.True,
                    slack,
                    Text(value, "name") ?? Text(value, "display_name")));
            }
            return selections;
        }

        // jsonb nests deeper than the parser's limit allows; such a value is dropped like any other malformed one.
        private static JsonDocument? TryParse(string json) {
            try {
                return JsonDocument.Parse(json);
            } catch (JsonException) {
                return null;
            }
        }

        private static decimal? Number(JsonElement value, string name) =>
            value.TryGetProperty(name, out var property) ? property.ValueKind switch {
                JsonValueKind.Number when property.TryGetDecimal(out var number) => number,
                JsonValueKind.String when decimal.TryParse(property.GetString(), NumberStyles.Number, CultureInfo.InvariantCulture, out var number) => number,
                _ => null,
            } : null;

        private static string? Text(JsonElement value, string name) =>
            value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    /// <summary>A vote as <c>poker_votes</c> stores it; every imported vote is locked.</summary>
    internal readonly record struct VoteValue(short? Points, short? High) {
        public static readonly VoteValue Abstain = new(null, null);

        public bool Abstained => Points is null;
    }

    /// <summary>
    /// Selection keys → principals: an imported user (following merges), <c>slack_*</c> → one external principal per
    /// key, anything else → <c>v5(guest:key)</c> named by the latest name seen for it (scrubbed). UUID-shaped keys are
    /// lower-cased first, so a guest is the same principal whichever case its browser stored.
    /// </summary>
    private sealed class Voters(PrincipalDirectory principals) {
        private readonly Dictionary<Guid, (string Kind, string? Name, DateTime SeenAt)> _synthetic = [];

        public Guid Resolve(string key, string? name, DateTime seenAt) {
            if (Guid.TryParseExact(key, "D", out var id)) {
                return principals.User(id) ?? Synthetic(SyntheticIds.Guest(id), "guest", name, seenAt);
            }
            return key.StartsWith("slack_", StringComparison.Ordinal)
                ? Synthetic(SyntheticIds.Slack(key), "external", SlackDisplayName, seenAt)
                : Synthetic(SyntheticIds.Guest(key), "guest", name, seenAt);
        }

        public Guid Observer(Guid id) => principals.User(id) ?? Synthetic(SyntheticIds.Guest(id), "guest", null, DateTime.MinValue);

        public IEnumerable<PrincipalRow> Synthetic(IEnumerable<Guid> referenced) =>
            referenced.Distinct().Where(_synthetic.ContainsKey).Order()
                .Select(x => new PrincipalRow(x, _synthetic[x].Kind, DisplayNames.Scrub(_synthetic[x].Name)));

        private Guid Synthetic(Guid id, string kind, string? name, DateTime seenAt) {
            if (!_synthetic.TryGetValue(id, out var known) || (!string.IsNullOrWhiteSpace(name) && (known.Name is null || seenAt >= known.SeenAt))) {
                _synthetic[id] = (kind, string.IsNullOrWhiteSpace(name) ? known.Name : name, seenAt);
            }
            return id;
        }
    }

    /// <summary>
    /// <c>code = coalesce(room_id, id::text)</c>. A room id that is not a valid code, or that an earlier session already
    /// took exactly, falls back to the session id (reported). When an earlier session's room id is this session's id
    /// text, the fallback is the id without hyphens, then that with a counter. Case-insensitive collisions are kept,
    /// since an exact lookup wins, and reported.
    /// </summary>
    private sealed partial class SessionCodes {
        private readonly Dictionary<string, Guid> _codes = new(StringComparer.Ordinal);

        public string Assign(Guid session, string? roomId, ImportReport report) {
            var code = roomId ?? session.ToString();
            if (!Pattern().IsMatch(code) || !_codes.TryAdd(code, session)) {
                report.Note("pokerReplacedCodes", new JsonObject {
                    ["session"] = session.ToString(),
                    ["roomId"] = roomId,
                    ["reason"] = Pattern().IsMatch(code) ? "duplicate" : "invalid",
                });
                code = Fallbacks(session).First(x => _codes.TryAdd(x, session));
            }
            return code;
        }

        private static IEnumerable<string> Fallbacks(Guid session) {
            yield return session.ToString("D");
            yield return session.ToString("N");
            for (var i = 1; ; i++) {
                yield return $"{session:N}-{i}";
            }
        }

        public void ReportCollisions(ImportReport report) {
            foreach (var group in _codes.GroupBy(x => x.Key.ToUpperInvariant()).Where(x => x.Count() > 1).OrderBy(x => x.Key, StringComparer.Ordinal)) {
                report.Note("pokerCodeCollisions", new JsonObject {
                    ["code"] = group.Key,
                    ["sessions"] = new JsonArray([.. group.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => (JsonNode)x.Value.ToString())]),
                });
            }
        }

        [GeneratedRegex("^[A-Za-z0-9-]{4,36}\\z", RegexOptions.CultureInvariant)]
        private static partial Regex Pattern();
    }

    private sealed record PrincipalRow(Guid Id, string Kind, string DisplayName);

    private sealed record SessionRow(
        Guid Id,
        string Code,
        Guid? TeamId,
        bool PresenceEnabled,
        Guid? CurrentRoundId,
        int NextRoundNumber,
        DateTime LastActivityAt,
        DateTime CreatedAt);

    private sealed record RoundRow(
        Guid Id,
        Guid SessionId,
        int Number,
        int SortKey,
        string? TicketKey,
        string? ParentKey,
        bool Revealed,
        bool IsActive,
        bool AutoReveal,
        short? WinnerPoints,
        DateTime? RevealedAt,
        DateTime CreatedAt,
        IReadOnlyList<VoteRow> Votes);

    private sealed record VoteRow(Guid RoundId, Guid Principal, VoteValue Vote, DateTime UpdatedAt);

    private sealed class Load {
        public List<PrincipalRow> Principals { get; } = [];

        public List<SessionRow> Sessions { get; } = [];

        public List<(Guid Session, Guid Principal)> Observers { get; } = [];

        public List<RoundRow> Rounds { get; } = [];

        public async Task WriteAsync(NpgsqlConnection target, CancellationToken cancellationToken) {
            // Retro guests share the v5(guest:…) namespace and may already exist; the first writer's name stands.
            await using (var command = new NpgsqlCommand(
                "insert into principals (id, kind, display_name) select * from unnest(@ids, @kinds, @names) on conflict (id) do nothing", target)) {
                command.Parameters.Add(new NpgsqlParameter<Guid[]>("ids", [.. Principals.Select(x => x.Id)]));
                command.Parameters.Add(new NpgsqlParameter<string[]>("kinds", [.. Principals.Select(x => x.Kind)]));
                command.Parameters.Add(new NpgsqlParameter<string[]>("names", [.. Principals.Select(x => x.DisplayName)]));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await CopyAsync(target, "poker_sessions (id, code, team_id, presence_enabled, current_round_id, next_round_number, last_activity_at, created_at)",
                Sessions, async (writer, x, ct) => {
                    await writer.WriteAsync(x.Id, NpgsqlDbType.Uuid, ct);
                    await writer.WriteAsync(x.Code, NpgsqlDbType.Text, ct);
                    await WriteNullableAsync(writer, x.TeamId, NpgsqlDbType.Uuid, ct);
                    await writer.WriteAsync(x.PresenceEnabled, NpgsqlDbType.Boolean, ct);
                    await WriteNullableAsync(writer, x.CurrentRoundId, NpgsqlDbType.Uuid, ct);
                    await writer.WriteAsync(x.NextRoundNumber, NpgsqlDbType.Integer, ct);
                    await writer.WriteAsync(x.LastActivityAt, NpgsqlDbType.TimestampTz, ct);
                    await writer.WriteAsync(x.CreatedAt, NpgsqlDbType.TimestampTz, ct);
                }, cancellationToken);

            await CopyAsync(target, "poker_rounds (id, session_id, number, sort_key, ticket_key, parent_key, state, is_active, auto_reveal, winner_points, revealed_at, created_at)",
                Rounds, async (writer, x, ct) => {
                    await writer.WriteAsync(x.Id, NpgsqlDbType.Uuid, ct);
                    await writer.WriteAsync(x.SessionId, NpgsqlDbType.Uuid, ct);
                    await writer.WriteAsync(x.Number, NpgsqlDbType.Integer, ct);
                    await writer.WriteAsync(x.SortKey, NpgsqlDbType.Integer, ct);
                    await WriteNullableAsync(writer, x.TicketKey, NpgsqlDbType.Text, ct);
                    await WriteNullableAsync(writer, x.ParentKey, NpgsqlDbType.Text, ct);
                    await writer.WriteAsync(x.Revealed ? "revealed" : "voting", NpgsqlDbType.Text, ct);
                    await writer.WriteAsync(x.IsActive, NpgsqlDbType.Boolean, ct);
                    await writer.WriteAsync(x.AutoReveal, NpgsqlDbType.Boolean, ct);
                    await WriteNullableAsync(writer, x.WinnerPoints, NpgsqlDbType.Smallint, ct);
                    await WriteNullableAsync(writer, x.RevealedAt, NpgsqlDbType.TimestampTz, ct);
                    await writer.WriteAsync(x.CreatedAt, NpgsqlDbType.TimestampTz, ct);
                }, cancellationToken);

            await CopyAsync(target, "poker_votes (round_id, principal_id, points, high_points, locked, abstained, updated_at)",
                Rounds.SelectMany(x => x.Votes), async (writer, x, ct) => {
                    await writer.WriteAsync(x.RoundId, NpgsqlDbType.Uuid, ct);
                    await writer.WriteAsync(x.Principal, NpgsqlDbType.Uuid, ct);
                    await WriteNullableAsync(writer, x.Vote.Points, NpgsqlDbType.Smallint, ct);
                    await WriteNullableAsync(writer, x.Vote.High, NpgsqlDbType.Smallint, ct);
                    await writer.WriteAsync(true, NpgsqlDbType.Boolean, ct);
                    await writer.WriteAsync(x.Vote.Abstained, NpgsqlDbType.Boolean, ct);
                    await writer.WriteAsync(x.UpdatedAt, NpgsqlDbType.TimestampTz, ct);
                }, cancellationToken);

            await CopyAsync(target, "poker_observers (session_id, principal_id)", Observers, async (writer, x, ct) => {
                await writer.WriteAsync(x.Session, NpgsqlDbType.Uuid, ct);
                await writer.WriteAsync(x.Principal, NpgsqlDbType.Uuid, ct);
            }, cancellationToken);
        }

        private static async Task CopyAsync<T>(
            NpgsqlConnection target,
            string table,
            IEnumerable<T> rows,
            Func<NpgsqlBinaryImporter, T, CancellationToken, Task> write,
            CancellationToken cancellationToken) {
            await using var writer = await target.BeginBinaryImportAsync($"copy {table} from stdin (format binary)", cancellationToken);
            foreach (var row in rows) {
                await writer.StartRowAsync(cancellationToken);
                await write(writer, row, cancellationToken);
            }
            await writer.CompleteAsync(cancellationToken);
        }

        private static Task WriteNullableAsync<T>(NpgsqlBinaryImporter writer, T? value, NpgsqlDbType type, CancellationToken cancellationToken) where T : struct =>
            value is { } present ? writer.WriteAsync(present, type, cancellationToken) : writer.WriteNullAsync(cancellationToken);

        private static Task WriteNullableAsync(NpgsqlBinaryImporter writer, string? value, NpgsqlDbType type, CancellationToken cancellationToken) =>
            value is not null ? writer.WriteAsync(value, type, cancellationToken) : writer.WriteNullAsync(cancellationToken);
    }
}
