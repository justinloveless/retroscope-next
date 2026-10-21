using Retroscope.Core.Protocol;

namespace Retroscope.Core.Retro;

/// <summary>
/// <c>retro.config.update</c> semantics: a null field is "not changing", and <c>maxVotes: 0</c> means unlimited
/// because a JSON null cannot (U2 decision 5).
/// </summary>
public static class RetroConfigPatches {
    private static readonly Func<RetroConfigPatch, object?>[] Fields = [
        p => p.AllowAnonymous,
        p => p.VotingEnabled,
        p => p.MaxVotes,
        p => p.ShowAuthorNames,
        p => p.StagesEnabled,
        p => p.EnforceReadiness,
        p => p.AllowSelfVotes,
        p => p.VoteEmoji,
        p => p.SortChronologically,
    ];

    public static RetroConfigPatch FromConfig(RetroConfig config) {
        return new RetroConfigPatch(
            config.AllowAnonymous,
            config.VotingEnabled,
            config.MaxVotes ?? 0,
            config.ShowAuthorNames,
            config.StagesEnabled,
            config.EnforceReadiness,
            config.AllowSelfVotes,
            config.VoteEmoji,
            config.SortChronologically);
    }

    public static bool ExpectedCoversPatch(RetroConfigPatch patch, RetroConfigPatch expected) {
        return Fields.All(field => field(patch) is null || field(expected) is not null);
    }

    /// <summary>Every field the patch changes was, as the client saw it, what it is now.</summary>
    public static bool ExpectedMatches(RetroConfigPatch expected, RetroConfigPatch patch, RetroConfigPatch current) {
        return Fields.All(field => field(patch) is null || Equals(field(expected), field(current)));
    }

    public static RetroConfig Apply(RetroConfig config, RetroConfigPatch patch) {
        return new RetroConfig(
            patch.AllowAnonymous ?? config.AllowAnonymous,
            patch.VotingEnabled ?? config.VotingEnabled,
            patch.ShowAuthorNames ?? config.ShowAuthorNames,
            patch.StagesEnabled ?? config.StagesEnabled,
            patch.EnforceReadiness ?? config.EnforceReadiness,
            patch.AllowSelfVotes ?? config.AllowSelfVotes,
            patch.VoteEmoji ?? config.VoteEmoji,
            patch.SortChronologically ?? config.SortChronologically,
            patch.MaxVotes is { } max ? max == 0 ? null : max : config.MaxVotes);
    }
}
