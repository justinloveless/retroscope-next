using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Retro;

namespace Retroscope.Core.Tests.Retro;

/// <summary>
/// A C# stand-in for the client reducer (<c>web/src/retro/model.js</c>): folds tx events into a snapshot plus
/// overlay. Tests check that the fold always equals a fresh snapshot, which is the property the golden
/// transcripts hand to the JS reducers. One-shot signals (the timer's <c>expired</c>) are not state.
/// </summary>
internal sealed class RetroViewerModel {
    private readonly RetroSnapshot _base;
    private readonly List<ColumnInfo> _columns;
    private readonly Dictionary<CardId, CardInfo> _cards;
    private readonly Dictionary<CommentId, CommentInfo> _comments;
    private readonly Dictionary<CardId, RetroVoteChanged> _votes;
    private readonly Dictionary<EndorsementId, EndorsementInfo> _endorsements;
    private readonly HashSet<CardId> _mineCards;
    private readonly HashSet<CommentId> _mineComments;
    private readonly HashSet<CardId> _myVotes;
    private RetroBoardMeta _meta;
    private RetroConfig _config;
    private RetroStage _stage;
    private RetroTimer _timer;
    private RetroReadiness _readiness;
    private CardId? _focus;
    private int _maxEndorsements;
    private List<MemberInfo> _members;

    public RetroViewerModel(RetroSnapshot snapshot, RetroOverlay overlay) {
        _base = snapshot;
        _meta = snapshot.Meta;
        _config = snapshot.Config;
        _stage = snapshot.Stage;
        _timer = Normalize(snapshot.Timer);
        _readiness = snapshot.Readiness;
        _focus = snapshot.Focus;
        _maxEndorsements = snapshot.MaxEndorsements;
        _members = snapshot.Members.ToList();
        _columns = [.. snapshot.Columns];
        _cards = snapshot.Cards.ToDictionary(c => c.Id);
        _comments = snapshot.Comments.ToDictionary(c => c.Id);
        _votes = snapshot.Votes.ToDictionary(v => v.CardId);
        _endorsements = snapshot.Endorsements.ToDictionary(e => e.Id);
        _mineCards = [.. overlay.MineCards];
        _mineComments = [.. overlay.MineComments];
        _myVotes = [.. overlay.MyVotes];
    }

    public void Apply(WireEvent evt) {
        switch (evt) {
            case RetroCardAdded e:
                _cards[e.Card.Id] = e.Card with { Mine = null };
                SetMember(_mineCards, e.Card.Id, e.Card.Mine);
                break;
            case RetroCardEdited e:
                _cards[e.CardId] = _cards[e.CardId] with { Body = e.Body, Version = e.Version };
                break;
            case RetroCardDeleted e:
                RemoveCard(e.CardId);
                break;
            case RetroVoteChanged e:
                if (e.Count == 0) {
                    _votes.Remove(e.CardId);
                } else {
                    _votes[e.CardId] = e with { Mine = null };
                }
                SetMember(_myVotes, e.CardId, e.Mine);
                break;
            case RetroCommentAdded e:
                _comments[e.Comment.Id] = e.Comment with { Mine = null };
                SetMember(_mineComments, e.Comment.Id, e.Comment.Mine);
                break;
            case RetroCommentDeleted e:
                _comments.Remove(e.CommentId);
                _mineComments.Remove(e.CommentId);
                break;
            case RetroColumnAdded e:
                _columns.Add(e.Column);
                break;
            case RetroColumnUpdated e:
                _columns[_columns.FindIndex(c => c.Id == e.Column.Id)] = e.Column;
                break;
            case RetroColumnDeleted e:
                _columns.RemoveAll(c => c.Id == e.ColumnId);
                foreach (var card in _cards.Values.Where(c => c.ColumnId == e.ColumnId).Select(c => c.Id).ToList()) {
                    RemoveCard(card);
                }
                break;
            case RetroColumnOrder e:
                var byId = _columns.ToDictionary(c => c.Id);
                _columns.Clear();
                _columns.AddRange(e.Order.Select(id => byId[id]));
                break;
            case RetroActionItemsColumn e:
                for (var i = 0; i < _columns.Count; i++) {
                    _columns[i] = _columns[i] with { ActionItems = _columns[i].Id == e.ColumnId };
                }
                break;
            case RetroBoardMeta e:
                _meta = e;
                break;
            case RetroConfigChanged e:
                _config = e.Config;
                break;
            case RetroStageChanged e:
                _stage = e.Stage;
                break;
            case RetroReadiness e:
                _readiness = e;
                break;
            case RetroFocus e:
                _focus = e.CardId;
                break;
            case RetroTimer e:
                _timer = Normalize(e);
                break;
            case RetroEndorsementAdded e:
                _endorsements[e.Endorsement.Id] = e.Endorsement;
                break;
            case RetroEndorsementRemoved e:
                _endorsements.Remove(e.EndorsementId);
                break;
            case RetroMembers e:
                _members = [.. e.Members];
                break;
            case TeamSettingsChanged e:
                _maxEndorsements = e.MaxEndorsements ?? _maxEndorsements;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(evt), evt.GetType().Name, "Not a board event.");
        }
    }

    /// <summary>The state as a snapshot and overlay in the server's canonical order, as JSON.</summary>
    public string ToJson() {
        var cards = _cards.Values.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id.Value).ToList();
        var snapshot = _base with {
            Meta = _meta,
            Config = _config,
            Stage = _stage,
            Timer = _timer,
            Columns = _columns.ToList(),
            Cards = cards,
            Comments = _comments.Values.OrderBy(c => c.CreatedAt).ThenBy(c => c.Id.Value).ToList(),
            Votes = cards.Where(c => _votes.ContainsKey(c.Id)).Select(c => _votes[c.Id]).ToList(),
            Readiness = _readiness,
            Focus = _focus,
            Endorsements = _endorsements.Values.OrderBy(e => e.CreatedAt).ThenBy(e => e.Id.Value).ToList(),
            MaxEndorsements = _maxEndorsements,
            Members = _members,
        };
        var comments = snapshot.Comments.Select(c => c.Id).ToList();
        var overlay = new RetroOverlay(
            cards.Select(c => c.Id).Where(_mineCards.Contains).ToList(),
            comments.Where(_mineComments.Contains).ToList(),
            cards.Select(c => c.Id).Where(_myVotes.Contains).ToList());
        return Canonical(snapshot, overlay);
    }

    public static string Canonical(RetroSnapshot snapshot, RetroOverlay overlay) {
        return RetroFixture.Json(snapshot with { Timer = Normalize(snapshot.Timer) }) + "\n" + RetroFixture.Json(overlay);
    }

    private static RetroTimer Normalize(RetroTimer timer) {
        return timer.State == TimerState.Idle ? timer with { EndsAt = null, RemainingMs = null, Expired = null } : timer;
    }

    private static void SetMember<T>(HashSet<T> set, T item, bool? member) {
        if (member == true) {
            set.Add(item);
        } else if (member == false) {
            set.Remove(item);
        }
    }

    private void RemoveCard(CardId card) {
        _cards.Remove(card);
        _votes.Remove(card);
        _mineCards.Remove(card);
        _myVotes.Remove(card);
        foreach (var comment in _comments.Values.Where(c => c.CardId == card).Select(c => c.Id).ToList()) {
            _comments.Remove(comment);
            _mineComments.Remove(comment);
        }
    }
}
