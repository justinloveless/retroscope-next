using Retroscope.Core.Ids;
using Retroscope.Core.Markup;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Core.Retro;

// Cards, comments and votes: open to every participant, subject to the stage gates and ownership.
public sealed partial class RetroDecider {
    private Decision<RetroEvent> AddCard(RetroState state, RetroCardAdd command, Principal who, CommandContext context) {
        if (state.Cards.ContainsKey(command.CardId)) {
            return Reject(NackCode.Invalid, "The card id is taken.");
        }
        if (state.FindColumn(command.ColumnId) is not { } column) {
            return Reject(NackCode.NotFound, "No such column.");
        }
        if (!StageRules.AllowsContent(state.Config, state.Stage, column.ActionItems)) {
            return Reject(NackCode.StageForbids);
        }
        if (command.Anonymous && !MayPostAnonymously(state, who)) {
            return Reject(NackCode.Forbidden, "Anonymous posting is not open to this caller.");
        }
        if (CheckBody(state, command.Body, MarkupField.Card, context.Access, out var mentions, out var images) is { } rejection) {
            return rejection;
        }
        var authorship = AuthorshipOf(state, who, command.Anonymous);
        ActionItemInfo? actionItem = null;
        if (column.ActionItems && state.TeamId is not null) {
            actionItem = new ActionItemInfo(
                ActionItemIdFor(command.CardId),
                command.Body,
                SourceVersion: 1,
                CreatedAt: context.Now.ToUnixTimeMilliseconds(),
                CommentCount: 0,
                SourceBoardId: state.BoardId,
                SourceCardId: command.CardId,
                CreatedBy: command.Anonymous ? null : who.Id);
        }
        return Accept(new CardAdded(
            command.CardId,
            command.ColumnId,
            command.Body,
            authorship,
            context.Now,
            mentions,
            images,
            actionItem));
    }

    private Decision<RetroEvent> EditCard(RetroState state, RetroCardEdit command, Principal who, IAccessView access) {
        if (!state.Cards.TryGetValue(command.CardId, out var card)) {
            return Reject(NackCode.NotFound, "No such card.");
        }
        if (!RetroRoles.Owns(state, card.Authorship, who.Id, tagger)) {
            return Reject(NackCode.Forbidden, "Only the author edits a card.");
        }
        if (!StageRules.AllowsContent(state.Config, state.Stage, IsActionItemsColumn(state, card.ColumnId))) {
            return Reject(NackCode.StageForbids);
        }
        if (command.BaseVersion != card.Version) {
            return Stale(writer => {
                writer.WriteStartObject();
                writer.WriteString("body", card.Body);
                writer.WriteNumber("version", card.Version);
                writer.WriteEndObject();
            });
        }
        if (string.Equals(command.Body, card.Body, StringComparison.Ordinal)) {
            return NoOp;
        }
        if (CheckBody(state, command.Body, MarkupField.Card, access, out var mentions, out var images) is { } rejection) {
            return rejection;
        }
        return Accept(new CardEdited(command.CardId, command.Body, card.Version + 1, card.Authorship.Anonymous, mentions, images));
    }

    private Decision<RetroEvent> DeleteCard(RetroState state, RetroCardDelete command, Principal who, IAccessView access) {
        if (!state.Cards.TryGetValue(command.CardId, out var card)) {
            return Reject(NackCode.NotFound, "No such card.");
        }
        var actionItems = IsActionItemsColumn(state, card.ColumnId);
        var owner = RetroRoles.Owns(state, card.Authorship, who.Id, tagger);
        if (!owner && !(actionItems && RetroRoles.IsFacilitator(state, who, access))) {
            return Reject(NackCode.Forbidden, "Only the author, or a facilitator in the action-items column, deletes a card.");
        }
        if (!StageRules.AllowsContent(state.Config, state.Stage, actionItems)) {
            return Reject(NackCode.StageForbids);
        }
        var deleted = new CardDeleted(command.CardId, CommentsOf(state, [command.CardId]));
        // A focused card that disappears takes the focus with it in the same transaction (R21).
        return state.Focus == command.CardId ? Accept(deleted, new FocusChanged(null)) : Accept(deleted);
    }

    private Decision<RetroEvent> SetVote(RetroState state, RetroVoteSet command, Principal who) {
        if (!state.Cards.TryGetValue(command.CardId, out var card)) {
            return Reject(NackCode.NotFound, "No such card.");
        }
        if (!state.Config.VotingEnabled) {
            return Reject(NackCode.Forbidden, "Voting is off on this board.");
        }
        if (!StageRules.AllowsVoting(state.Config, state.Stage)) {
            return Reject(NackCode.StageForbids);
        }
        if (card.Voters.Contains(who.Id) == command.On) {
            return NoOp;
        }
        if (command.On) {
            if (!state.Config.AllowSelfVotes && RetroRoles.Owns(state, card.Authorship, who.Id, tagger)) {
                return Reject(NackCode.SelfVote);
            }
            if (state.Config.MaxVotes is { } max && state.VotesBy(who.Id) >= max) {
                return Reject(NackCode.VoteLimit);
            }
        }
        return Accept(new VoteSet(command.CardId, who.Id, command.On));
    }

    private Decision<RetroEvent> AddComment(RetroState state, RetroCommentAdd command, Principal who, CommandContext context) {
        if (state.Comments.ContainsKey(command.CommentId)) {
            return Reject(NackCode.Invalid, "The comment id is taken.");
        }
        if (!state.Cards.TryGetValue(command.CardId, out var card)) {
            return Reject(NackCode.NotFound, "No such card.");
        }
        if (!StageRules.AllowsContent(state.Config, state.Stage, IsActionItemsColumn(state, card.ColumnId))) {
            return Reject(NackCode.StageForbids);
        }
        if (command.Anonymous && !MayPostAnonymously(state, who)) {
            return Reject(NackCode.Forbidden, "Anonymous posting is not open to this caller.");
        }
        if (CheckBody(state, command.Body, MarkupField.Comment, context.Access, out var mentions, out var images) is { } rejection) {
            return rejection;
        }
        return Accept(new CommentAdded(
            command.CommentId,
            command.CardId,
            command.Body,
            AuthorshipOf(state, who, command.Anonymous),
            context.Now,
            mentions,
            images));
    }

    private Decision<RetroEvent> DeleteComment(RetroState state, RetroCommentDelete command, Principal who) {
        if (!state.Comments.TryGetValue(command.CommentId, out var comment)) {
            return Reject(NackCode.NotFound, "No such comment.");
        }
        if (!RetroRoles.Owns(state, comment.Authorship, who.Id, tagger)) {
            return Reject(NackCode.Forbidden, "Only the author deletes a comment.");
        }
        var card = state.Cards[comment.CardId];
        if (!StageRules.AllowsContent(state.Config, state.Stage, IsActionItemsColumn(state, card.ColumnId))) {
            return Reject(NackCode.StageForbids);
        }
        return Accept(new CommentDeleted(command.CommentId, comment.CardId));
    }

    private static List<CommentId> CommentsOf(RetroState state, IReadOnlyCollection<CardId> cards) {
        return state.Comments.Values
            .Where(comment => cards.Contains(comment.CardId))
            .Select(comment => comment.Id)
            .ToList();
    }
}
