using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Security;

namespace Retroscope.Core.Retro;

/// <summary>
/// What each viewer may see of a board. The shared snapshot comes in two variants, because the roster,
/// endorsements and team settings go to team members only (S12); everything else that differs per viewer
/// (<c>mine</c>, own votes, the recipient's <c>celebrate</c>) travels in the overlay or in that viewer's events.
/// </summary>
public sealed class RetroProjection(IAnonTagger tagger) {
    /// <summary>Whether <paramref name="viewer"/> gets the members-only parts (pass the decide-time membership).</summary>
    public static bool SeesRoster(RetroState state, bool viewerIsMember) => state.TeamId is not null && viewerIsMember;

    public static RetroSnapshot Snapshot(RetroState state, IReadOnlyList<PrincipalInfo> presence, bool forMembers) {
        var roster = SeesRoster(state, forMembers);
        var cards = CardsInOrder(state);
        return new RetroSnapshot(
            state.BoardId,
            state.Code,
            Meta(state),
            state.Config,
            state.Stage,
            TimerRules.ToWire(state.Timer),
            state.Columns.Select(column => column.ToInfo()).ToList(),
            cards.Select(card => CardInfo(state, card, mine: null)).ToList(),
            CommentsInOrder(state).Select(comment => CommentInfo(state, comment, mine: null)).ToList(),
            cards.Where(card => card.Voters.Count > 0).Select(card => Votes(state, card, mine: null)).ToList(),
            Readiness(state),
            presence,
            state.TeamId,
            state.Focus,
            Members: roster ? state.Members.ToList() : null,
            Endorsements: roster ? state.Endorsements.Values.OrderBy(e => e.CreatedAt).ThenBy(e => e.Id.Value).ToList() : null,
            EndorsementTypes: roster ? state.EndorsementTypes.ToList() : null,
            MaxEndorsements: roster ? state.MaxEndorsements : null);
    }

    public RetroOverlay Overlay(RetroState state, PrincipalId viewer) {
        var cards = CardsInOrder(state);
        return new RetroOverlay(
            cards.Where(card => Owns(state, card.Authorship, viewer)).Select(card => card.Id).ToList(),
            CommentsInOrder(state).Where(comment => Owns(state, comment.Authorship, viewer)).Select(comment => comment.Id).ToList(),
            cards.Where(card => card.Voters.Contains(viewer)).Select(card => card.Id).ToList());
    }

    /// <summary>
    /// The tx frame's events for one viewer, projected from the state after <paramref name="events"/> were
    /// applied. A members-only event projects to nothing for everyone else; the seq still advances.
    /// </summary>
    public IReadOnlyList<WireEvent> ForViewer(RetroState state, IReadOnlyList<RetroEvent> events, PrincipalId viewer, bool viewerIsMember) {
        var roster = SeesRoster(state, viewerIsMember);
        var wire = new List<WireEvent>();
        foreach (var evt in events) {
            Project(state, evt, viewer, roster, wire);
        }
        return wire;
    }

    private void Project(RetroState state, RetroEvent evt, PrincipalId viewer, bool roster, List<WireEvent> wire) {
        switch (evt) {
            case CardAdded e:
                var card = state.Cards[e.CardId];
                wire.Add(new RetroCardAdded(CardInfo(state, card, Owns(state, card.Authorship, viewer))));
                break;
            case CardEdited e:
                wire.Add(new RetroCardEdited(e.CardId, e.Body, e.Version));
                break;
            case CardDeleted e:
                wire.Add(new RetroCardDeleted(e.CardId));
                break;
            case VoteSet e:
                wire.Add(Votes(state, state.Cards[e.CardId], state.Cards[e.CardId].Voters.Contains(viewer)));
                break;
            case CommentAdded e:
                var comment = state.Comments[e.CommentId];
                wire.Add(new RetroCommentAdded(CommentInfo(state, comment, Owns(state, comment.Authorship, viewer))));
                break;
            case CommentDeleted e:
                wire.Add(new RetroCommentDeleted(e.CommentId, e.CardId));
                break;
            case ColumnAdded e:
                wire.Add(new RetroColumnAdded(state.FindColumn(e.ColumnId)!.ToInfo()));
                break;
            case ColumnUpdated e:
                wire.Add(new RetroColumnUpdated(state.FindColumn(e.ColumnId)!.ToInfo()));
                break;
            case ColumnDeleted e:
                wire.Add(new RetroColumnDeleted(e.ColumnId));
                break;
            case ColumnsReordered e:
                wire.Add(new RetroColumnOrder(e.Order));
                break;
            case ActionItemsColumnSet e:
                wire.Add(new RetroActionItemsColumn(e.ColumnId));
                break;
            case BoardRenamed or AccessChanged:
                wire.Add(Meta(state));
                break;
            case ArchivedChanged e:
                wire.Add(Meta(state));
                AddVotesIfVisibilityChanged(state, StageRules.ShowsVoters(state.Config, state.Stage, !e.Archived), viewer, wire);
                break;
            case ConfigChanged e:
                wire.Add(new RetroConfigChanged(e.Config));
                if (e.Previous.ShowAuthorNames != e.Config.ShowAuthorNames) {
                    AddAuthorship(state, viewer, wire);
                }
                AddVotesIfVisibilityChanged(state, StageRules.ShowsVoters(e.Previous, state.Stage, state.Archived), viewer, wire);
                break;
            case StageChanged e:
                wire.Add(new RetroStageChanged(e.To));
                wire.Add(Readiness(state));
                AddVotesIfVisibilityChanged(state, StageRules.ShowsVoters(state.Config, e.From, state.Archived), viewer, wire);
                break;
            case ReadySet:
                wire.Add(Readiness(state));
                break;
            case FocusChanged e:
                wire.Add(new RetroFocus(e.CardId));
                break;
            case TimerChanged e:
                wire.Add(TimerRules.ToWire(e.Timer, e.ExpiredAt));
                break;
            case EndorsementAdded e when roster:
                wire.Add(new RetroEndorsementAdded(e.Endorsement, e.Endorsement.To == viewer ? true : null));
                break;
            case EndorsementRemoved e when roster:
                wire.Add(new RetroEndorsementRemoved(e.EndorsementId));
                break;
            case MaxEndorsementsChanged e when roster:
                wire.Add(new TeamSettingsChanged(MaxEndorsements: e.MaxEndorsements));
                break;
            case MembersReplaced when roster:
                wire.Add(new RetroMembers(state.Members.ToList()));
                break;
            case EndorsementAdded or EndorsementRemoved or MaxEndorsementsChanged or MembersReplaced:
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(evt), evt.GetType().Name, "Unknown retro event.");
        }
    }

    /// <summary>
    /// Turning author names on or off changes every card and comment; they are sent again as <c>added</c>,
    /// which the client applies as an upsert by id.
    /// </summary>
    private void AddAuthorship(RetroState state, PrincipalId viewer, List<WireEvent> wire) {
        foreach (var card in CardsInOrder(state)) {
            wire.Add(new RetroCardAdded(CardInfo(state, card, Owns(state, card.Authorship, viewer))));
        }
        foreach (var comment in CommentsInOrder(state)) {
            wire.Add(new RetroCommentAdded(CommentInfo(state, comment, Owns(state, comment.Authorship, viewer))));
        }
    }

    /// <summary>When voter identities appear or disappear, every voted card is sent again with or without them.</summary>
    private static void AddVotesIfVisibilityChanged(RetroState state, bool shownBefore, PrincipalId viewer, List<WireEvent> wire) {
        if (shownBefore == StageRules.ShowsVoters(state)) {
            return;
        }
        foreach (var card in CardsInOrder(state).Where(card => card.Voters.Count > 0)) {
            wire.Add(Votes(state, card, card.Voters.Contains(viewer)));
        }
    }

    private bool Owns(RetroState state, Authorship authorship, PrincipalId viewer) => RetroRoles.Owns(state, authorship, viewer, tagger);

    private static RetroBoardMeta Meta(RetroState state) => new(state.Title, state.Visibility, state.Archived);

    private static RetroReadiness Readiness(RetroState state) {
        return new RetroReadiness(state.Stage, state.ReadyThisStage.OrderBy(principal => principal.Value).ToList());
    }

    private static RetroVoteChanged Votes(RetroState state, Card card, bool? mine) {
        var voters = StageRules.ShowsVoters(state) ? card.Voters.OrderBy(voter => voter.Value).ToList() : null;
        return new RetroVoteChanged(card.Id, card.Voters.Count, voters, mine);
    }

    /// <summary>Anonymous posts never carry an author; with author names off, no post does.</summary>
    private static AuthorInfo? VisibleAuthor(RetroState state, Authorship authorship) {
        return state.Config.ShowAuthorNames ? authorship.Author : null;
    }

    private static CardInfo CardInfo(RetroState state, Card card, bool? mine) {
        return new CardInfo(
            card.Id,
            card.ColumnId,
            card.Body,
            card.Authorship.Anonymous,
            card.Version,
            card.CreatedAt.ToUnixTimeMilliseconds(),
            VisibleAuthor(state, card.Authorship),
            mine);
    }

    private static CommentInfo CommentInfo(RetroState state, Comment comment, bool? mine) {
        return new CommentInfo(
            comment.Id,
            comment.CardId,
            comment.Body,
            comment.Authorship.Anonymous,
            comment.CreatedAt.ToUnixTimeMilliseconds(),
            VisibleAuthor(state, comment.Authorship),
            mine);
    }

    private static List<Card> CardsInOrder(RetroState state) {
        return state.Cards.Values.OrderBy(card => card.CreatedAt).ThenBy(card => card.Id.Value).ToList();
    }

    private static List<Comment> CommentsInOrder(RetroState state) {
        return state.Comments.Values.OrderBy(comment => comment.CreatedAt).ThenBy(comment => comment.Id.Value).ToList();
    }
}
