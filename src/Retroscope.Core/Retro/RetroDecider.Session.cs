using Retroscope.Core.Protocol;
using Retroscope.Core.Room;

namespace Retroscope.Core.Retro;

// Readiness, focus, the shared timer and endorsements.
public sealed partial class RetroDecider {
    private static Decision<RetroEvent> SetReady(RetroState state, RetroReadySet command, Principal who, IAccessView access) {
        if (!RetroRoles.MayPace(state, who, access)) {
            return Reject(NackCode.Forbidden, "Team members only.");
        }
        if (command.Stage != state.Stage) {
            return Stale(state.Stage, WireJsonContext.Default.RetroStage);
        }
        if (state.ReadyThisStage.Contains(who.Id) == command.Ready) {
            return NoOp;
        }
        return Accept(new ReadySet(command.Stage, who.Id, command.Ready));
    }

    /// <summary>Any participant who may pace the board; the client drops its intent silently on <c>stale</c>.</summary>
    private static Decision<RetroEvent> SetFocus(RetroState state, RetroFocusSet command, Principal who, IAccessView access) {
        if (!RetroRoles.MayPace(state, who, access)) {
            return Reject(NackCode.Forbidden, "Team members only.");
        }
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

    /// <summary>The timer follows the facilitator rule: signed in, and a member on team boards.</summary>
    private static Decision<RetroEvent> RunTimer(RetroState state, Principal who, IAccessView access, Func<TimerStep> step) {
        if (!RetroRoles.IsFacilitator(state, who, access)) {
            return Reject(NackCode.Forbidden, "Sign in to use the timer.");
        }
        return FromTimer(state, step());
    }

    private static Decision<RetroEvent> GiveEndorsement(RetroState state, RetroEndorseGive command, Principal who, CommandContext context) {
        if (state.TeamId is null) {
            return Reject(NackCode.Invalid, "Endorsements need a team board.");
        }
        if (!RetroRoles.IsMember(state, who, context.Access)) {
            return Reject(NackCode.Forbidden, "Team members only.");
        }
        if (command.ToUserId == who.Id || !RetroRoles.IsMember(state, command.ToUserId, context.Access)) {
            return Reject(NackCode.Invalid, "Endorse another team member.");
        }
        if (state.EndorsementTypes.Find(type => type.Id == command.TypeId) is not { Archived: false }) {
            return Reject(NackCode.NotFound, "No such endorsement type.");
        }
        if (state.Endorsements.ContainsKey(command.EndorsementId)) {
            return Reject(NackCode.Invalid, "The endorsement id is taken.");
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

    private static Decision<RetroEvent> RevokeEndorsement(RetroState state, RetroEndorseRevoke command, Principal who, IAccessView access) {
        if (!RetroRoles.IsMember(state, who, access)) {
            return Reject(NackCode.Forbidden, "Team members only.");
        }
        if (!state.Endorsements.TryGetValue(command.EndorsementId, out var endorsement)) {
            return Reject(NackCode.NotFound, "No such endorsement.");
        }
        if (endorsement.From != who.Id) {
            return Reject(NackCode.Forbidden, "Only the giver withdraws an endorsement.");
        }
        return Accept(new EndorsementRemoved(command.EndorsementId));
    }
}
