using System.Collections.Frozen;
using Retroscope.Core.Ids;

namespace Retroscope.Core.Room;

/// <summary>
/// Who is in the room right now. <see cref="Principals"/> is grace-smoothed: a principal whose last
/// connection closed less than the grace period ago still counts. <see cref="ClientsOf"/> lists the tabs
/// that are actually connected, which is what spotlight binding needs (R7).
/// </summary>
public sealed class PresenceView {
    public static readonly PresenceView Empty = new(
        FrozenSet<PrincipalId>.Empty,
        FrozenDictionary<PrincipalId, FrozenSet<ClientId>>.Empty);

    private readonly FrozenDictionary<PrincipalId, FrozenSet<ClientId>> _clients;

    public PresenceView(FrozenSet<PrincipalId> principals, FrozenDictionary<PrincipalId, FrozenSet<ClientId>> clients) {
        Principals = principals;
        _clients = clients;
    }

    public FrozenSet<PrincipalId> Principals { get; }

    public bool IsPresent(PrincipalId principal) => Principals.Contains(principal);

    public IReadOnlySet<ClientId> ClientsOf(PrincipalId principal) {
        return _clients.TryGetValue(principal, out var clients) ? clients : FrozenSet<ClientId>.Empty;
    }

    public bool IsConnected(PrincipalId principal, ClientId client) {
        return _clients.TryGetValue(principal, out var clients) && clients.Contains(client);
    }
}

public enum PresenceChange : byte {
    /// <summary>First connection of a principal that was not present (no pending leave).</summary>
    Joined,

    /// <summary>The principal's grace period after its last connection expired.</summary>
    Left,

    /// <summary>A tab of an already present principal connected.</summary>
    ClientConnected,

    /// <summary>A tab stayed disconnected for the client grace period.</summary>
    ClientGone,

    /// <summary>Display name or avatar changed.</summary>
    Updated,
}

public sealed record PresenceDelta(PresenceChange Change, PrincipalId Principal, ClientId? Client = null);
