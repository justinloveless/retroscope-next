using Retroscope.Core.Protocol;

namespace Retroscope.Core.Retro;

/// <summary>The stage gates of DESIGN §c.4. They apply only when stages are enabled.</summary>
public static class StageRules {
    /// <summary>Card add, edit and delete, and comment add and delete, in a column of that kind.</summary>
    public static bool AllowsContent(RetroConfig config, RetroStage stage, bool actionItemsColumn) {
        if (!config.StagesEnabled) {
            return true;
        }
        return stage switch {
            RetroStage.Thinking => true,
            RetroStage.Discussing => actionItemsColumn,
            _ => false,
        };
    }

    public static bool AllowsVoting(RetroConfig config, RetroStage stage) {
        return config.VotingEnabled && (!config.StagesEnabled || stage == RetroStage.Voting);
    }

    public static bool AllowsFocus(RetroConfig config, RetroStage stage) {
        return !config.StagesEnabled || stage == RetroStage.Discussing;
    }

    /// <summary>Whether projections carry who voted for each card.</summary>
    public static bool ShowsVoters(RetroConfig config, RetroStage stage, bool archived) {
        return archived || !config.StagesEnabled || stage is RetroStage.Discussing or RetroStage.Closed;
    }

    public static bool ShowsVoters(RetroState state) => ShowsVoters(state.Config, state.Stage, state.Archived);

    /// <summary>The stage that "advance" leads to; closed has none.</summary>
    public static RetroStage? Next(RetroStage stage) {
        return stage switch {
            RetroStage.Thinking => RetroStage.Voting,
            RetroStage.Voting => RetroStage.Discussing,
            RetroStage.Discussing => RetroStage.Closed,
            _ => null,
        };
    }

    /// <summary>
    /// With readiness enforced only these moves are open: advancing one stage, Reset and Restart (to thinking)
    /// and Reopen (closed to discussing). Only advancing waits for everyone to be ready.
    /// </summary>
    public static bool IsAdvance(RetroStage from, RetroStage to) => Next(from) == to;

    public static bool IsAlwaysAllowed(RetroStage from, RetroStage to) {
        return to == RetroStage.Thinking || from == RetroStage.Closed && to == RetroStage.Discussing;
    }
}
