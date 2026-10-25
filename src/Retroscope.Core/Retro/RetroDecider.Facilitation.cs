using Retroscope.Core.Ids;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Core.Retro;

// Columns, title, configuration and stages: the facilitation every member has (UX18).
public sealed partial class RetroDecider {
    private const int ColumnTitleLength = 100;
    private const int BoardTitleLength = 200;
    private const int VoteEmojiLength = 8;
    private const int MaxVotesLimit = 99;

    private static Decision<RetroEvent> AddColumn(RetroState state, RetroColumnAdd command) {
        // A resend of a create that already committed acks without applying twice; a column renamed or recoloured
        // since then can no longer be compared.
        if (state.FindColumn(command.ColumnId) is { } existing) {
            return existing.Version > 1
                || (string.Equals(existing.Title, command.Title, StringComparison.Ordinal) && existing.Color == command.Color)
                ? NoOp
                : Reject(NackCode.Invalid, "The column id is taken.");
        }
        if (!IsPlainText(command.Title, ColumnTitleLength)) {
            return Reject(NackCode.Invalid, "Column titles are 1 to 100 characters.");
        }
        var sortKey = state.Columns.Count == 0 ? 0 : state.Columns.Max(column => column.SortKey) + 1;
        return Accept(new ColumnAdded(command.ColumnId, command.Title, command.Color, sortKey));
    }

    private static Decision<RetroEvent> UpdateColumn(RetroState state, RetroColumnUpdate command) {
        if (state.FindColumn(command.ColumnId) is not { } column) {
            return Reject(NackCode.NotFound, "No such column.");
        }
        if (command.BaseVersion != column.Version) {
            return Stale(column.ToInfo(), WireJsonContext.Default.ColumnInfo);
        }
        if (command.Title is { } title && !IsPlainText(title, ColumnTitleLength)) {
            return Reject(NackCode.Invalid, "Column titles are 1 to 100 characters.");
        }
        var nextTitle = command.Title ?? column.Title;
        var nextColor = command.Color ?? column.Color;
        if (string.Equals(nextTitle, column.Title, StringComparison.Ordinal) && nextColor == column.Color) {
            return NoOp;
        }
        return Accept(new ColumnUpdated(column.Id, nextTitle, nextColor, column.Version + 1));
    }

    private static Decision<RetroEvent> DeleteColumn(RetroState state, RetroColumnDelete command) {
        if (state.FindColumn(command.ColumnId) is null) {
            return Reject(NackCode.NotFound, "No such column.");
        }
        var cards = state.Cards.Values.Where(card => card.ColumnId == command.ColumnId).Select(card => card.Id).ToList();
        var deleted = new ColumnDeleted(command.ColumnId, cards, CommentsOf(state, cards));
        return state.Focus is { } focus && cards.Contains(focus) ? Accept(deleted, new FocusChanged(null)) : Accept(deleted);
    }

    private static Decision<RetroEvent> ReorderColumns(RetroState state, RetroColumnReorder command) {
        var current = state.Columns.Select(column => column.Id).ToList();
        var isPermutation = command.Order.Count == current.Count
            && command.Order.Distinct().Count() == current.Count
            && command.Order.All(current.Contains);
        if (!isPermutation) {
            return Stale(writer => {
                writer.WriteStartArray();
                foreach (var id in current) {
                    writer.WriteStringValue(id.Value);
                }
                writer.WriteEndArray();
            });
        }
        return command.Order.SequenceEqual(current) ? NoOp : Accept(new ColumnsReordered(command.Order.ToList()));
    }

    private static Decision<RetroEvent> SetActionItemsColumn(RetroState state, RetroColumnSetActionItems command) {
        if (command.ColumnId is { } id && state.FindColumn(id) is null) {
            return Reject(NackCode.NotFound, "No such column.");
        }
        return state.ActionItemsColumn?.Id == command.ColumnId ? NoOp : Accept(new ActionItemsColumnSet(command.ColumnId));
    }

    private static Decision<RetroEvent> Rename(RetroState state, RetroBoardRename command) {
        if (!string.Equals(command.Expected, state.Title, StringComparison.Ordinal)) {
            return Stale(state.Title, WireJsonContext.Default.String);
        }
        if (!IsPlainText(command.Title, BoardTitleLength)) {
            return Reject(NackCode.Invalid, "Board titles are 1 to 200 characters.");
        }
        return string.Equals(command.Title, state.Title, StringComparison.Ordinal) ? NoOp : Accept(new BoardRenamed(command.Title));
    }

    /// <summary>
    /// Only the fields present in the patch apply, and each must come with the value the client last saw, so a
    /// settings dialog opened before someone else's change cannot overwrite it (R5).
    /// </summary>
    private static Decision<RetroEvent> UpdateConfig(RetroState state, RetroConfigUpdate command) {
        var current = state.Config;
        var currentAsPatch = RetroConfigPatches.FromConfig(current);
        var patch = command.Patch;
        if (!RetroConfigPatches.ExpectedCoversPatch(patch, command.Expected)) {
            return Reject(NackCode.Invalid, "Every patched field needs its expected value.");
        }
        if (!RetroConfigPatches.ExpectedMatches(command.Expected, patch, currentAsPatch)) {
            return Stale(current, WireJsonContext.Default.RetroConfig);
        }
        if (patch.MaxVotes is < 0 or > MaxVotesLimit) {
            return Reject(NackCode.Invalid, "maxVotes is 0 (unlimited) to 99.");
        }
        if (patch.VoteEmoji is { } emoji && !IsPlainText(emoji, VoteEmojiLength)) {
            return Reject(NackCode.Invalid, "The vote emoji is 1 to 8 characters.");
        }
        var next = RetroConfigPatches.Apply(current, patch);
        return next == current ? NoOp : Accept(new ConfigChanged(next, current));
    }

    private static Decision<RetroEvent> SetStage(RetroState state, RetroStageSet command, CommandContext context) {
        if (command.From != state.Stage) {
            return Stale(state.Stage, WireJsonContext.Default.RetroStage);
        }
        if (command.To == state.Stage) {
            return NoOp;
        }
        var config = state.Config;
        if (config.StagesEnabled && config.EnforceReadiness && !StageRules.IsAlwaysAllowed(command.From, command.To)) {
            if (!StageRules.IsAdvance(command.From, command.To)) {
                return Reject(NackCode.StageForbids, "With readiness enforced the board only advances one stage.");
            }
            // Readiness counts present principals only: someone who left cannot hold the room hostage.
            if (!context.Presence.Principals.All(state.ReadyThisStage.Contains)) {
                return Reject(NackCode.StageForbids, "Not everyone present is ready.");
            }
        }
        return Accept(new StageChanged(command.From, command.To));
    }

    /// <summary>Not blank, at most <paramref name="maxLength"/> code points, and storable as Postgres text.</summary>
    private static bool IsPlainText(string text, int maxLength) {
        if (string.IsNullOrWhiteSpace(text)) {
            return false;
        }
        var length = 0;
        for (var i = 0; i < text.Length; i++) {
            var c = text[i];
            if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) {
                i++;
            } else if (char.IsSurrogate(c) || char.IsControl(c)) {
                return false;
            }
            length++;
        }
        return length <= maxLength;
    }
}
