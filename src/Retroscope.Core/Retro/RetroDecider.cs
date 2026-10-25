using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Retroscope.Core.Ids;
using Retroscope.Core.Markup;
using Retroscope.Core.Protocol;
using Retroscope.Core.Room;
using Retroscope.Core.Security;

namespace Retroscope.Core.Retro;

/// <summary>
/// The board's domain (DESIGN §c.4 Retro). Authorization comes first: every client command needs a member of the
/// board's team, resolved through <see cref="CommandContext.Access"/> at decide time (R12, UX18). Internal inputs were
/// authorized by whoever posted them.
/// </summary>
public sealed partial class RetroDecider(IAnonTagger tagger) : IRoomDomain<RetroState, RetroInput, RetroEvent> {
    private const string TimerKey = "retro.timer";

    private static readonly Decision<RetroEvent> NoOp = new Decision<RetroEvent>.NoOp();

    // Action items created in place get an id derived from their source card, so a retried command cannot
    // create a second item and no randomness enters the decider.
    private static readonly Guid ActionItemNamespace = new("0b5e7a39-55a4-4c8e-9f0e-3d8a6c1f2b77");

    public Decision<RetroEvent> Decide(RetroState state, RetroInput input, CommandContext context) {
        return input switch {
            RetroInput.Client client => DecideClient(state, client.Command, context),
            RetroInput.TimerDue due => FromTimer(state, TimerRules.Expire(state.Timer, due.EndsAt, context.Now)),
            RetroInput.TeamSettingsChanged settings => settings.MaxEndorsements == state.MaxEndorsements
                ? NoOp
                : Accept(new MaxEndorsementsChanged(settings.MaxEndorsements)),
            RetroInput.MembersChanged members => members.Members.SequenceEqual(state.Members)
                ? NoOp
                : Accept(new MembersReplaced(members.Members)),
            RetroInput.ActionItemCommentAdd comment => AddActionItemComment(state, comment, context),
            RetroInput.ArchiveSet archive => archive.Archived == state.Archived
                ? NoOp
                : Accept(new ArchivedChanged(archive.Archived, archive.By, context.Now)),
            RetroInput.Delete => state.Deleted ? NoOp : Accept(new BoardDeleted()),
            _ => throw new ArgumentOutOfRangeException(nameof(input), input.GetType().Name, "Unknown retro input."),
        };
    }

    public void Apply(RetroState state, RetroEvent evt) => RetroApply.Apply(state, evt);

    public IReadOnlyList<RetroEvent> OnPresence(RetroState state, PresenceDelta delta, PresenceView presence, DateTimeOffset now) => [];

    public IReadOnlyList<TimerRequest<RetroInput>> Timers(RetroState state, PresenceView presence, DateTimeOffset now) {
        // A timer that ended while the server was down is due at once on load, so it expires exactly once (R16).
        return state.Timer is { State: TimerState.Running, EndsAt: { } endsAt }
            ? [new TimerRequest<RetroInput>(TimerKey, endsAt, new RetroInput.TimerDue(endsAt))]
            : [];
    }

    public IReadOnlyList<RetroEvent> OnWarm(RetroState state, PresenceView presence, DateTimeOffset now) => [];

    private Decision<RetroEvent> DecideClient(RetroState state, RetroCommand command, CommandContext context) {
        if (context.Viewer is not { Who: var who }) {
            return Reject(NackCode.Forbidden, "Commands need a caller.");
        }
        // Every participant is a member, and every member may do everything on an open board (UX18).
        if (!RetroRoles.IsMember(state, who, context.Access)) {
            return Reject(NackCode.Forbidden, "Team members only.");
        }
        if (state.Archived) {
            return Reject(NackCode.BoardArchived);
        }
        return command switch {
            RetroCardAdd c => AddCard(state, c, who, context),
            RetroCardEdit c => EditCard(state, c, who, context.Access),
            RetroCardDelete c => DeleteCard(state, c, who),
            RetroVoteSet c => SetVote(state, c, who),
            RetroCommentAdd c => AddComment(state, c, who, context),
            RetroCommentDelete c => DeleteComment(state, c, who),
            RetroColumnAdd c => AddColumn(state, c),
            RetroColumnUpdate c => UpdateColumn(state, c),
            RetroColumnDelete c => DeleteColumn(state, c),
            RetroColumnReorder c => ReorderColumns(state, c),
            RetroColumnSetActionItems c => SetActionItemsColumn(state, c),
            RetroBoardRename c => Rename(state, c),
            RetroConfigUpdate c => UpdateConfig(state, c),
            RetroStageSet c => SetStage(state, c, context),
            RetroReadySet c => SetReady(state, c, who),
            RetroFocusSet c => SetFocus(state, c),
            RetroTimerStart c => FromTimer(state, TimerRules.Start(state.Timer, c.From, c.DurationMs, c.Alarm, c.Music, context.Now)),
            RetroTimerPause c => FromTimer(state, TimerRules.Pause(state.Timer, c.From, context.Now)),
            RetroTimerResume c => FromTimer(state, TimerRules.Resume(state.Timer, c.From, context.Now)),
            RetroTimerReset c => FromTimer(state, TimerRules.Reset(state.Timer, c.From)),
            RetroTimerSetFlags c => FromTimer(state, TimerRules.SetFlags(state.Timer, c.Alarm, c.Music)),
            RetroEndorseGive c => GiveEndorsement(state, c, who, context),
            RetroEndorseRevoke c => RevokeEndorsement(state, c, who),
            _ => Reject(NackCode.Invalid, $"{command.GetType().Name} is not a board command."),
        };
    }

    private static Decision<RetroEvent> Accept(params RetroEvent[] events) => new Decision<RetroEvent>.Accept(events);

    private static Decision<RetroEvent> Reject(NackCode code, string? detail = null) => new Decision<RetroEvent>.Reject(code, detail);

    private static Decision<RetroEvent> Stale<T>(T current, JsonTypeInfo<T> type) {
        return Stale(writer => JsonSerializer.Serialize(writer, current, type));
    }

    /// <summary>A <c>stale</c> nack whose <c>d</c> is <c>{"current": …}</c> (R5).</summary>
    private static Decision<RetroEvent> Stale(Action<Utf8JsonWriter> writeCurrent) {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer)) {
            writer.WriteStartObject();
            writer.WritePropertyName("current");
            writeCurrent(writer);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(buffer.WrittenMemory);
        return new Decision<RetroEvent>.Reject(NackCode.Stale, Data: document.RootElement.Clone());
    }

    private static Decision<RetroEvent> FromTimer(RetroState state, TimerStep step) {
        return step.Verdict switch {
            TimerVerdict.Changed => Accept(new TimerChanged(step.Next!, step.ExpiredAt)),
            TimerVerdict.Unchanged => NoOp,
            TimerVerdict.Stale => Stale(TimerRules.ToWire(state.Timer), WireJsonContext.Default.RetroTimer),
            _ => Reject(NackCode.Invalid, "The timer cannot make that move."),
        };
    }

    internal static NackCode ToNack(MarkupError error) {
        return error is MarkupError.TooLong or MarkupError.TooManyImages ? NackCode.TooLarge : NackCode.Invalid;
    }

    /// <summary>
    /// Validates a stored body. On success returns the mentioned team members (a mention of anyone else records
    /// nothing) and the board images it references.
    /// </summary>
    private static Decision<RetroEvent>? CheckBody(
        RetroState state,
        string body,
        MarkupField field,
        IAccessView access,
        out IReadOnlyList<PrincipalId> mentions,
        out IReadOnlyList<BlobId> images) {
        mentions = [];
        images = [];
        var check = MarkupLimits.Check(body, field);
        if (check.Error is { } error) {
            return Reject(ToNack(error), $"Body is {error}.");
        }
        var doc = check.Doc!;
        mentions = Mentions.Extract(doc)
            .Select(PrincipalId.From)
            .Where(principal => RetroRoles.IsMember(state, principal, access))
            .ToList();
        images = doc.Descendants().OfType<DocImage>().Select(image => BlobId.From(image.BlobId)).Distinct().ToList();
        return null;
    }

    /// <summary>
    /// Only members post, and members are users, so a new post never carries the guest badge; imported and earlier
    /// guest posts keep theirs.
    /// </summary>
    private Authorship AuthorshipOf(RetroState state, Principal who, bool anonymous) {
        return anonymous
            ? new Authorship(null, tagger.Tag(state.BoardId, who.Id))
            : new Authorship(new AuthorInfo(who.DisplayName, Guest: false, who.Id, who.AvatarBlobId), null);
    }

    private static bool IsActionItemsColumn(RetroState state, ColumnId column) => state.FindColumn(column)?.ActionItems == true;

    private static ActionItemId ActionItemIdFor(CardId card) => ActionItemId.From(UuidV5.Create(ActionItemNamespace, card.ToString()));
}
