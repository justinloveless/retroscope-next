using Retroscope.Importer.Map;

namespace Retroscope.Importer.Verify;

/// <summary>
/// Exactly one owner per team that has members (§h.5, U39 decision 11). A team whose members were all deleted has
/// no owner and is allowed; U39 lists those as <c>teamsWithoutMembers</c>.
/// </summary>
public sealed class OwnerCheck : IVerifyCheck {
    public string Name => "one-owner";

    public Task<IReadOnlyList<string>> RunAsync(VerifyContext context, CancellationToken cancellationToken) =>
        TargetQueries.FailuresAsync(context.Target, """
            select 'team ' || m.team_id || ' has ' || count(*) filter (where m.role = 'owner') || ' owners'
            from team_members m
            group by m.team_id
            having count(*) filter (where m.role = 'owner') <> 1
            order by 1
            """, cancellationToken);
}

/// <summary>Every card's column, and every vote's and comment's board, agree with the card's board (§h.5).</summary>
public sealed class BoardConsistencyCheck : IVerifyCheck {
    public string Name => "board-consistency";

    public Task<IReadOnlyList<string>> RunAsync(VerifyContext context, CancellationToken cancellationToken) =>
        TargetQueries.FailuresAsync(context.Target, """
            select 'card ' || c.id || ' is on board ' || c.board_id || ' but its column is on ' || k.board_id
            from retro_cards c join retro_columns k on k.id = c.column_id
            where k.board_id <> c.board_id
            union all
            select 'vote of ' || v.voter_id || ' on card ' || v.card_id || ' is on board ' || v.board_id || ' but the card is on ' || c.board_id
            from retro_votes v join retro_cards c on c.id = v.card_id
            where v.board_id <> c.board_id
            union all
            select 'comment ' || m.id || ' is on board ' || m.board_id || ' but its card is on ' || c.board_id
            from retro_comments m join retro_cards c on c.id = m.card_id
            where m.board_id <> c.board_id
            order by 1
            """, cancellationToken);
}

/// <summary>
/// Poker rounds (§h.5): <c>(session, number)</c> unique, each session's sort keys dense from 0 (the stores index
/// <c>Order</c> by them, U21 decision 6), and no unlocked vote on a revealed round.
/// </summary>
public sealed class PokerRoundsCheck : IVerifyCheck {
    public string Name => "poker-rounds";

    public Task<IReadOnlyList<string>> RunAsync(VerifyContext context, CancellationToken cancellationToken) =>
        TargetQueries.FailuresAsync(context.Target, """
            select 'session ' || session_id || ' has ' || count(*) || ' rounds numbered ' || number
            from poker_rounds group by session_id, number having count(*) > 1
            union all
            select 'session ' || session_id || ' has sort keys ' || string_agg(sort_key::text, ',' order by sort_key) || ', not 0..' || (count(*) - 1)
            from poker_rounds group by session_id
            having min(sort_key) <> 0 or max(sort_key) <> count(*) - 1 or count(distinct sort_key) <> count(*)
            union all
            select 'round ' || r.id || ' is revealed but the vote of ' || v.principal_id || ' is unlocked'
            from poker_votes v join poker_rounds r on r.id = v.round_id
            where r.state = 'revealed' and not v.locked
            order by 1
            """, cancellationToken);
}

/// <summary>No principal's display name looks like an email address (S27).</summary>
public sealed class DisplayNameCheck : IVerifyCheck {
    public string Name => "display-names";

    public async Task<IReadOnlyList<string>> RunAsync(VerifyContext context, CancellationToken cancellationToken) {
        var rows = await TargetQueries.RowsAsync(context.Target,
            "select id, display_name from principals where display_name like '%@%' order by id", cancellationToken);
        return TargetQueries.Summarize([.. rows
            .Where(x => DisplayNames.LooksLikeEmail((string)x[1]!))
            .Select(x => $"principal {x[0]} is named like an email address")]);
    }
}

/// <summary>
/// Anonymity across tables (S6): nothing reachable from an anonymous card or comment names a principal. A blob one
/// references has no <c>created_by</c>, and an action item copied from an anonymous card has no <c>created_by</c>.
/// "Anonymous" is a tag, or a card with neither an author nor a legacy author.
/// </summary>
public sealed class AnonymityCheck : IVerifyCheck {
    public string Name => "anonymity";

    public Task<IReadOnlyList<string>> RunAsync(VerifyContext context, CancellationToken cancellationToken) =>
        TargetQueries.FailuresAsync(context.Target, """
            with anonymous as (
              select id, 'card' as kind, body from retro_cards where anon_tag is not null or (author_id is null and legacy_author is null)
              union all
              select id, 'comment', body from retro_comments where anon_tag is not null)
            select 'blob ' || b.id || ' in anonymous ' || a.kind || ' ' || a.id || ' names its creator'
            from blobs b join anonymous a on a.body like '%blob:' || b.id || ')%'
            where b.created_by is not null
            union all
            select 'action item ' || t.id || ' of anonymous card ' || a.id || ' names its creator'
            from team_action_items t join anonymous a on a.kind = 'card' and a.id = t.source_card_id
            where t.created_by is not null
            order by 1
            """, cancellationToken);
}
