using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;

namespace Retroscope.Core.Poker;

/// <summary>
/// What a poker actor decides: a client command, or an internal transition (timer, actor post, HTTP ask).
/// Internal transitions cannot derive from <see cref="PokerCommand"/>, because every concrete command type in Core
/// must have a catalog kind; a client command converts implicitly.
/// </summary>
public abstract record PokerInput {
    public static implicit operator PokerInput(PokerCommand command) => new ClientCommand(command);
}

public sealed record ClientCommand(PokerCommand Command) : PokerInput;

/// <summary>Armed by <see cref="PokerReactions.Timers"/>; ignored unless <paramref name="Gen"/> is still current.</summary>
public sealed record AutoRevealDue(RoundId RoundId, int Gen) : PokerInput;

/// <summary>The holder principal has been absent for <see cref="PokerTiming.SpotlightGrace"/>.</summary>
public sealed record SpotlightGraceDue(PrincipalId Holder) : PokerInput;

/// <summary>The spotlight stopped moving for <see cref="PokerTiming.SpotlightSettle"/>: persist its position.</summary>
public sealed record SpotlightSettleDue : PokerInput;

/// <summary>The off-actor Jira write of a <c>poker.jira.submitPoints</c> finished.</summary>
public sealed record JiraPointsResult(
    RoundId RoundId,
    string Key,
    decimal Points,
    PrincipalId By,
    bool Ok,
    string? Code = null) : PokerInput;

/// <summary>Issue metadata from the Jira cache; only parent keys reach the state (titles are never persisted).</summary>
public sealed record JiraMetaLoaded(IReadOnlyList<JiraMeta> Issues) : PokerInput;

public sealed record RosterChanged(IReadOnlyList<PrincipalInfo> Seats) : PokerInput;

public sealed record PointDescriptionsChanged(IReadOnlyDictionary<string, string> PointDescriptions) : PokerInput;

public sealed record JiraSettingsChanged(TeamJiraSettings Settings) : PokerInput;

/// <summary>HTTP delete of the whole session; <see cref="Room.CommandContext.Viewer"/> is the caller.</summary>
public sealed record DeleteSession : PokerInput;
