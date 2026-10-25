using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Core.Retro;

// Readiness, focus and endorsements; the shared timer goes through FromTimer.
public sealed partial class RetroDecider {
    private static Decision<RetroEvent> SetReady(RetroState state, RetroReadySet command, Principal who) {
        if (command.Stage != state.Stage) {
            return Stale(state.Stage, WireJsonContext.Default.RetroStage);
        }
        if (state.ReadyThisStage.Contains(who.Id) == command.Ready) {
            return NoOp;
        }
        return Accept(new ReadySet(command.Stage, who.Id, command.Ready));
    }

    /// <summary>Any member; the client drops its intent silently on <c>stale</c>.</summary>
    private static Decision<RetroEvent> SetFocus(RetroState state, RetroFocusSet command) {
        if (!StageRules.AllowsFocus(state.Config, state.Stage)) {
            return Reject(NackCode.StageForbids);
        }
        if (command.From != state.Focus) {
            return Stale(writer => {
                if (state.Focus is { } focus) {
                    writer.WriteStringValue(focus.Value);
                } else {
                    writer.WriteNullValue();
                }
            });
        }
        if (command.CardId is { } card && !state.Cards.ContainsKey(card)) {
            return Reject(NackCode.NotFound, "No such card.");
        }
        return command.CardId == state.Focus ? NoOp : Accept(new FocusChanged(command.CardId));
    }

    private static Decision<RetroEvent> GiveEndorsement(RetroState state, RetroEndorseGive command, Principal who, CommandContext context) {
        // A resend of a gift that already committed acks without applying twice, even if the type was archived since.
        if (state.Endorsements.TryGetValue(command.EndorsementId, out var existing)) {
            return existing.From == who.Id && existing.To == command.ToUserId && existing.TypeId == command.TypeId
                ? NoOp
                : Reject(NackCode.Invalid, "The endorsement id is taken.");
        }
        if (command.ToUserId == who.Id || !RetroRoles.IsMember(state, command.ToUserId, context.Access)) {
            return Reject(NackCode.Invalid, "Endorse another team member.");
        }
        if (state.EndorsementTypes.Find(type => type.Id == command.TypeId) is not { Archived: false }) {
            return Reject(NackCode.NotFound, "No such endorsement type.");
        }
        var given = state.Endorsements.Values.Where(e => e.From == who.Id).ToList();
        if (given.Exists(e => e.To == command.ToUserId && e.TypeId == command.TypeId)) {
            return NoOp;
        }
        if (given.Count >= state.MaxEndorsements) {
            return Reject(NackCode.EndorseLimit);
        }
        var endorsement = new EndorsementInfo(
            command.EndorsementId,
            command.TypeId,
            who.Id,
            command.ToUserId,
            context.Now.ToUnixTimeMilliseconds());
        return Accept(new EndorsementAdded(endorsement));
    }

    private static Decision<RetroEvent> RevokeEndorsement(RetroState state, RetroEndorseRevoke command, Principal who) {
        if (!state.Endorsements.TryGetValue(command.EndorsementId, out var endorsement)) {
            return Reject(NackCode.NotFound, "No such endorsement.");
        }
        if (endorsement.From != who.Id) {
            return Reject(NackCode.Forbidden, "Only the giver withdraws an endorsement.");
        }
        return Accept(new EndorsementRemoved(command.EndorsementId));
    }
}
