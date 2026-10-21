using System.Text.Json;
using Retroscope.Core.Protocol;

namespace Retroscope.Core.Room;

public abstract record Decision<TEvent> {
    private Decision() {
    }

    public sealed record Accept(IReadOnlyList<TEvent> Events) : Decision<TEvent>;

    public sealed record NoOp : Decision<TEvent>;

    /// <param name="Detail">Human-readable message for the nack's <c>msg</c>.</param>
    /// <param name="Data">The nack's <c>d</c>, e.g. <c>{"current":…}</c> for <see cref="NackCode.Stale"/>.</param>
    public sealed record Reject(NackCode Code, string? Detail = null, JsonElement? Data = null) : Decision<TEvent>;
}
