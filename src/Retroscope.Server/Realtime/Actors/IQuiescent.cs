using System.Runtime.CompilerServices;

[assembly: InternalsVisibleTo("Retroscope.Sim")]

namespace Retroscope.Server.Realtime.Actors;

/// <summary>
/// Lets the deterministic simulation wait for quiet without sending the actor anything: a barrier message would
/// itself change behaviour (an idle actor does not retire while anything is queued, and any message reloads a
/// poisoned one).
/// </summary>
internal interface IQuiescent {
    /// <summary>Both queues are empty and the loop is waiting on both of them (a wait that completed means it woke).</summary>
    bool IsParked { get; }
}
