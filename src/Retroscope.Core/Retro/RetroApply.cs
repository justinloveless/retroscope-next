using Retroscope.Core.Ids;

namespace Retroscope.Core.Retro;

/// <summary>Folds committed events into the actor's state. Runs only after the transaction committed.</summary>
public static class RetroApply {
    public static void Apply(RetroState state, RetroEvent evt) {
        switch (evt) {
            case CardAdded e:
                state.Cards[e.CardId] = new Card {
                    Id = e.CardId,
                    ColumnId = e.ColumnId,
                    Body = e.Body,
                    CreatedAt = e.CreatedAt,
                    Authorship = e.Authorship,
                };
                break;
            case CardEdited e:
                var edited = state.Cards[e.CardId];
                edited.Body = e.Body;
                edited.Version = e.Version;
                break;
            case CardDeleted e:
                RemoveCards(state, [e.CardId]);
                break;
            case VoteSet e:
                var voters = state.Cards[e.CardId].Voters;
                if (e.On) {
                    voters.Add(e.Voter);
                } else {
                    voters.Remove(e.Voter);
                }
                break;
            case CommentAdded e:
                state.Comments[e.CommentId] = new Comment {
                    Id = e.CommentId,
                    CardId = e.CardId,
                    Body = e.Body,
                    CreatedAt = e.CreatedAt,
                    Authorship = e.Authorship,
                };
                break;
            case CommentDeleted e:
                state.Comments.Remove(e.CommentId);
                break;
            case ColumnAdded e:
                state.Columns.Add(new Column { Id = e.ColumnId, Title = e.Title, Color = e.Color, SortKey = e.SortKey });
                break;
            case ColumnUpdated e:
                var column = state.FindColumn(e.ColumnId)!;
                column.Title = e.Title;
                column.Color = e.Color;
                column.Version = e.Version;
                break;
            case ColumnDeleted e:
                RemoveCards(state, e.Cards);
                state.Columns.RemoveAll(c => c.Id == e.ColumnId);
                break;
            case ColumnsReordered e:
                var byId = state.Columns.ToDictionary(c => c.Id);
                state.Columns.Clear();
                for (var i = 0; i < e.Order.Count; i++) {
                    var moved = byId[e.Order[i]];
                    moved.SortKey = i;
                    state.Columns.Add(moved);
                }
                break;
            case ActionItemsColumnSet e:
                foreach (var c in state.Columns) {
                    c.ActionItems = c.Id == e.ColumnId;
                }
                break;
            case BoardRenamed e:
                state.Title = e.Title;
                break;
            case ConfigChanged e:
                state.Config = e.Config;
                break;
            case StageChanged e:
                state.Stage = e.To;
                state.ReadyThisStage.Clear();
                break;
            case ReadySet e:
                if (e.Ready) {
                    state.ReadyThisStage.Add(e.Principal);
                } else {
                    state.ReadyThisStage.Remove(e.Principal);
                }
                break;
            case FocusChanged e:
                state.Focus = e.CardId;
                break;
            case TimerChanged e:
                state.Timer = e.Timer;
                break;
            case EndorsementAdded e:
                state.Endorsements[e.Endorsement.Id] = e.Endorsement;
                break;
            case EndorsementRemoved e:
                state.Endorsements.Remove(e.EndorsementId);
                break;
            case MaxEndorsementsChanged e:
                state.MaxEndorsements = e.MaxEndorsements;
                break;
            case MembersReplaced e:
                state.Members.Clear();
                state.Members.AddRange(e.Members);
                break;
            case ArchivedChanged e:
                state.Archived = e.Archived;
                break;
            case BoardDeleted:
                state.Deleted = true;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(evt), evt.GetType().Name, "Unknown retro event.");
        }
    }

    private static void RemoveCards(RetroState state, IReadOnlyCollection<CardId> cards) {
        foreach (var card in cards) {
            state.Cards.Remove(card);
        }
        foreach (var comment in state.Comments.Values.Where(c => cards.Contains(c.CardId)).ToList()) {
            state.Comments.Remove(comment.Id);
        }
    }
}
