using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;

namespace Retroscope.Core.Tests.Poker;

/// <summary>
/// The client-side fold of a poker room, written as the rules <c>web/src/poker/model.js</c> must follow: start from
/// a snapshot and overlay, apply each tx frame's events, and track the round this viewer looks at. Folding every
/// projected frame must give exactly the snapshot and overlay the server would send now, which is what the
/// transcripts and the property tests check.
/// </summary>
internal sealed class ViewerModel {
    private readonly PrincipalId _me;
    private readonly List<RoundInfo> _rounds;
    private readonly Dictionary<(RoundId, PrincipalId), PokerVoteState> _public = [];
    private readonly Dictionary<(RoundId, PrincipalId), PokerVoteState> _own = [];
    private readonly Dictionary<RoundId, PokerJiraPoints> _submits = [];
    private PokerSnapshot _snapshot;
    private bool _heldByThisTab;

    public ViewerModel(PokerSnapshot snapshot, PokerOverlay overlay, PrincipalId me) {
        _me = me;
        _snapshot = snapshot;
        _rounds = [.. snapshot.Rounds];
        foreach (var vote in snapshot.Votes) {
            _public[(vote.RoundId, vote.Pid)] = vote;
        }
        foreach (var vote in overlay.Own) {
            _own[(vote.RoundId, vote.Pid)] = vote;
        }
        foreach (var submit in snapshot.JiraSubmits ?? []) {
            _submits[submit.RoundId] = submit;
        }
        _heldByThisTab = overlay.HeldByThisTab;
        Viewed = InitialRound(snapshot, me);
    }

    /// <summary>The round this viewer looks at.</summary>
    public RoundId? Viewed { get; private set; }

    /// <summary>
    /// A joiner lands on the spotlight round when following someone else's spotlight, otherwise on the pointer round
    /// (a <c>?round=</c> deep link wins over both, which the transcripts do not use).
    /// </summary>
    public static RoundId? InitialRound(PokerSnapshot snapshot, PrincipalId me) {
        if (snapshot.FollowSpotlight && snapshot.Spotlight is { } spotlight && spotlight.Holder != me) {
            return spotlight.RoundId;
        }
        return snapshot.CurrentRoundId ?? (snapshot.Rounds.Count > 0 ? snapshot.Rounds[0].Id : null);
    }

    /// <summary>
    /// Applies one tx frame and returns where it navigates this viewer, or null to stay. The rule (R13): a
    /// <c>poker.pointer</c> with <c>nav:true</c> moves everyone, followers of someone else's spotlight included; a
    /// change of the spotlight's round moves followers (not the holder) when follow is on; and a viewer whose round
    /// is gone at the end of the frame goes to the pointer round (which is what <c>nav:false</c> repairs mean).
    /// </summary>
    public RoundId? Apply(IReadOnlyList<WireEvent> events) {
        RoundId? target = null;
        foreach (var evt in events) {
            switch (evt) {
                case PokerPointer { Nav: true } pointer:
                    target = pointer.RoundId;
                    break;
                case PokerSpotlight spotlight
                    when spotlight.Holder is { } holder
                    && holder != _me
                    && spotlight.RoundId != _snapshot.Spotlight?.RoundId
                    && _snapshot.FollowSpotlight:
                    target = spotlight.RoundId;
                    break;
            }
            ApplyEvent(evt);
        }
        if (target is { } wanted && !Exists(wanted)) {
            target = null;
        }
        var current = target ?? Viewed;
        if (current is null || !Exists(current.Value)) {
            target = _snapshot.CurrentRoundId ?? (_rounds.Count > 0 ? _rounds[0].Id : null);
        }
        if (target is not null) {
            Viewed = target;
        }
        return target;
    }

    /// <summary>The folded state in the server's canonical form.</summary>
    public (PokerSnapshot Snapshot, PokerOverlay Overlay) View(IReadOnlyList<PrincipalInfo> presence) {
        var order = _rounds.Select((x, i) => (x.Id, i)).ToDictionary(x => x.Id, x => x.i);
        var snapshot = _snapshot with {
            Rounds = [.. _rounds],
            Votes = Ordered(_public.Values, order),
            Presence = presence.OrderBy(x => x.Pid.ToString(), StringComparer.Ordinal).ToArray(),
            JiraSubmits = _rounds.Where(x => _submits.ContainsKey(x.Id)).Select(x => _submits[x.Id]).ToArray(),
        };
        return (snapshot, new PokerOverlay(Ordered(_own.Values, order), _heldByThisTab));
    }

    private static PokerVoteState[] Ordered(IEnumerable<PokerVoteState> votes, Dictionary<RoundId, int> order) {
        return votes.OrderBy(x => order[x.RoundId]).ThenBy(x => x.Pid.ToString(), StringComparer.Ordinal).ToArray();
    }

    private bool Exists(RoundId id) => _rounds.Exists(x => x.Id == id);

    private int IndexOf(RoundId id) => _rounds.FindIndex(x => x.Id == id);

    private void ApplyEvent(WireEvent evt) {
        switch (evt) {
            case PokerVoteState { Status: VoteStatus.Empty } vote:
                _public.Remove((vote.RoundId, vote.Pid));
                _own.Remove((vote.RoundId, vote.Pid));
                break;
            case PokerVoteState { Status: null } own:
                _own[(own.RoundId, own.Pid)] = own;
                var status = own.Locked == true ? VoteStatus.Locked : VoteStatus.Selected;
                _public[(own.RoundId, own.Pid)] = new PokerVoteState(own.RoundId, own.Pid, Status: status);
                break;
            case PokerVoteState other:
                _public[(other.RoundId, other.Pid)] = other;
                break;
            case PokerRoundRevealed revealed:
                RemoveVotes(revealed.RoundId);
                foreach (var vote in revealed.Votes) {
                    _public[(revealed.RoundId, vote.Pid)] =
                        new PokerVoteState(revealed.RoundId, vote.Pid, vote.Points, vote.High, true, vote.Abstained);
                }
                var round = _rounds[IndexOf(revealed.RoundId)];
                _rounds[IndexOf(revealed.RoundId)] = round with {
                    State = RoundState.Revealed,
                    Winner = revealed.Winner,
                    RevealedAt = revealed.At,
                    RevealedBy = revealed.By,
                };
                break;
            case PokerRoundReplayed replayed:
                foreach (var key in _public.Keys.Where(x => x.Item1 == replayed.RoundId).ToArray()) {
                    _public[key] = new PokerVoteState(key.Item1, key.Item2, Status: VoteStatus.Locked);
                }
                break;
            case PokerRoundCreated created:
                _rounds.Add(created.Round);
                break;
            case PokerRoundUpdated updated:
                _rounds[IndexOf(updated.Round.Id)] = updated.Round;
                break;
            case PokerRoundDeleted deleted:
                _rounds.RemoveAt(IndexOf(deleted.RoundId));
                RemoveVotes(deleted.RoundId);
                _submits.Remove(deleted.RoundId);
                if (_snapshot.CurrentRoundId == deleted.RoundId) {
                    _snapshot = _snapshot with { CurrentRoundId = null };
                }
                break;
            case PokerRoundOrder reordered:
                var byId = _rounds.ToDictionary(x => x.Id);
                _rounds.Clear();
                _rounds.AddRange(reordered.Order.Select(x => byId[x]));
                break;
            case PokerRoundsReset reset:
                _rounds.Clear();
                _rounds.Add(reset.Round);
                _public.Clear();
                _own.Clear();
                _submits.Clear();
                _snapshot = _snapshot with { CurrentRoundId = null };
                break;
            case PokerPointer pointer:
                _snapshot = _snapshot with { CurrentRoundId = pointer.RoundId };
                break;
            case PokerSpotlight spotlight:
                _snapshot = _snapshot with {
                    Spotlight = spotlight is { Holder: { } holder, RoundId: { } id } ? new SpotlightInfo(holder, id) : null,
                };
                if (spotlight.Holder != _me) {
                    _heldByThisTab = false;
                }
                break;
            case PokerSettingsChanged settings:
                _snapshot = _snapshot with {
                    PresenceEnabled = settings.PresenceEnabled,
                    FollowSpotlight = settings.FollowSpotlight,
                };
                break;
            case PokerObservers observers:
                _snapshot = _snapshot with { Observers = observers.Ids };
                break;
            case PokerRoster roster:
                _snapshot = _snapshot with { Roster = roster.Seats };
                break;
            case TeamSettingsChanged { PointDescriptions: { } descriptions }:
                _snapshot = _snapshot with { PointDescriptions = descriptions };
                break;
            case PokerJiraPoints { Status: JiraSubmitStatus.Ok } done:
                _submits.Remove(done.RoundId);
                break;
            case PokerJiraPoints submit:
                _submits[submit.RoundId] = submit;
                break;
            case PokerSessionDeleted:
                break;
            default:
                throw new InvalidOperationException($"The model has no rule for {evt.GetType().Name}.");
        }
    }

    private void RemoveVotes(RoundId round) {
        foreach (var key in _public.Keys.Where(x => x.Item1 == round).ToArray()) {
            _public.Remove(key);
        }
        foreach (var key in _own.Keys.Where(x => x.Item1 == round).ToArray()) {
            _own.Remove(key);
        }
    }
}
