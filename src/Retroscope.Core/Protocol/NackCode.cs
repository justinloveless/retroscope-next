using System.Text.Json.Serialization;

namespace Retroscope.Core.Protocol;

/// <summary>Stable nack codes (DESIGN §c.6). Wire names never change once shipped.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<NackCode>))]
public enum NackCode : byte {
    [JsonStringEnumMemberName("forbidden")] Forbidden,
    [JsonStringEnumMemberName("not_subscribed")] NotSubscribed,
    [JsonStringEnumMemberName("not_found")] NotFound,
    [JsonStringEnumMemberName("invalid")] Invalid,
    [JsonStringEnumMemberName("stale")] Stale,
    [JsonStringEnumMemberName("round_revealed")] RoundRevealed,
    [JsonStringEnumMemberName("round_not_voting")] RoundNotVoting,
    [JsonStringEnumMemberName("round_inactive")] RoundInactive,
    [JsonStringEnumMemberName("round_pending")] RoundPending,
    [JsonStringEnumMemberName("vote_locked")] VoteLocked,
    [JsonStringEnumMemberName("observer")] Observer,
    [JsonStringEnumMemberName("stage_forbids")] StageForbids,
    [JsonStringEnumMemberName("board_archived")] BoardArchived,
    [JsonStringEnumMemberName("vote_limit")] VoteLimit,
    [JsonStringEnumMemberName("self_vote")] SelfVote,
    [JsonStringEnumMemberName("endorse_limit")] EndorseLimit,
    [JsonStringEnumMemberName("spotlight_held")] SpotlightHeld,
    [JsonStringEnumMemberName("not_holder")] NotHolder,
    [JsonStringEnumMemberName("jira_not_configured")] JiraNotConfigured,
    [JsonStringEnumMemberName("jira_pending")] JiraPending,
    [JsonStringEnumMemberName("too_large")] TooLarge,
    [JsonStringEnumMemberName("cid_reuse")] CidReuse,
    [JsonStringEnumMemberName("internal")] Internal,
    [JsonStringEnumMemberName("rate_limited")] RateLimited,
    [JsonStringEnumMemberName("server_busy")] ServerBusy,
    [JsonStringEnumMemberName("jira_unavailable")] JiraUnavailable,
}

public static class NackCodes {
    /// <summary>Retryable codes keep the client's outbox entry; every other code is final (R3).</summary>
    public static bool IsRetryable(this NackCode code) {
        return code is NackCode.RateLimited or NackCode.ServerBusy or NackCode.JiraUnavailable;
    }
}
