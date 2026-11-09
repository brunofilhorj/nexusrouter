using NexusRouter.Domain.Enums;
using NexusRouter.Domain.Models;

namespace NexusRouter.Domain.Abstractions;

/// <summary>
/// Per-client sequence counter and <c>.cidx</c> slots.
/// </summary>
public interface IClientIndex
{
    /// <summary>
    /// <c>DispatcherId</c> plus the client key.
    /// </summary>
    string ClientKey { get; }

    /// <summary>
    /// Next outbound sequence <see cref="ReserveSeq"/> will return.
    /// </summary>
    long NextOut { get; }

    /// <summary>
    /// Reserves the next outbound sequence.
    /// </summary>
    /// <returns>The sequence to put on the wire.</returns>
    /// <remarks>Zero allocation. Uses <see cref="Interlocked.Increment(ref long)"/>.</remarks>
    long ReserveSeq();

    /// <summary>
    /// Publishes an application slot. <c>kind</c> is written last.
    /// </summary>
    /// <param name="seq">Sequence returned by <see cref="ReserveSeq"/>.</param>
    /// <param name="logRef">Ingress record this outbound sequence carries.</param>
    void PublishApp(long seq, LogRef logRef);

    /// <summary>
    /// Publishes an administrative slot (<c>kind</c> 2) with no payload.
    /// </summary>
    /// <param name="seq">Sequence returned by <see cref="ReserveSeq"/>.</param>
    void PublishAdmin(long seq);

    /// <summary>
    /// Reads one slot.
    /// </summary>
    /// <param name="seq">Outbound sequence.</param>
    /// <param name="entry">The slot when the result is <see cref="IndexReadResult.Published"/>.</param>
    /// <returns>Published, not yet, or missing.</returns>
    IndexReadResult TryGet(long seq, out IndexEntry entry);
}
