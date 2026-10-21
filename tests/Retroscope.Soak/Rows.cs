using System.Text.Json;

namespace Retroscope.Soak;

internal sealed record PokerRoundRow(Guid Id, Guid SessionId, string State, string? TicketKey, DateTimeOffset? RevealedAt);

internal sealed record PokerVoteRow(Guid RoundId, Guid PrincipalId, int? Points, bool Locked, bool Abstained);

internal sealed record CardRow(Guid Id, Guid BoardId, string Body, int Version);

internal sealed record CommentRow(Guid Id, Guid BoardId);

internal sealed record CardVoteRow(Guid CardId, Guid VoterId);

/// <summary>The rows of the soak's rooms after the run: what the oracle compares the clients' acks with.</summary>
internal sealed record Rows(
    IReadOnlyList<PokerRoundRow> Rounds,
    IReadOnlyList<PokerVoteRow> Votes,
    IReadOnlyList<CardRow> Cards,
    IReadOnlyList<CommentRow> Comments,
    IReadOnlyList<CardVoteRow> CardVotes) {
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static async Task<Rows> LoadAsync(Stack stack, IReadOnlyList<Guid> sessions, IReadOnlyList<Guid> boards) {
        // Ids are Guids, so the array literals cannot carry anything but hex and hyphens.
        var s = $"'{{{string.Join(',', sessions)}}}'::uuid[]";
        var b = $"'{{{string.Join(',', boards)}}}'::uuid[]";
        return new Rows(
            await ReadAsync<PokerRoundRow>(stack, $"""
                select id, session_id as "sessionId", state, ticket_key as "ticketKey", revealed_at as "revealedAt"
                from poker_rounds where session_id = any({s})
                """),
            await ReadAsync<PokerVoteRow>(stack, $"""
                select v.round_id as "roundId", v.principal_id as "principalId", v.points, v.locked, v.abstained
                from poker_votes v join poker_rounds r on r.id = v.round_id where r.session_id = any({s})
                """),
            await ReadAsync<CardRow>(stack, $"""select id, board_id as "boardId", body, version from retro_cards where board_id = any({b})"""),
            await ReadAsync<CommentRow>(stack, $"""select id, board_id as "boardId" from retro_comments where board_id = any({b})"""),
            await ReadAsync<CardVoteRow>(stack, $"""
                select card_id as "cardId", voter_id as "voterId" from retro_votes where board_id = any({b})
                """));
    }

    private static async Task<IReadOnlyList<T>> ReadAsync<T>(Stack stack, string sql) {
        var json = await stack.PsqlAsync($"select coalesce(json_agg(t), '[]') from ({sql}) t");
        return JsonSerializer.Deserialize<List<T>>(json, Json)!;
    }
}
