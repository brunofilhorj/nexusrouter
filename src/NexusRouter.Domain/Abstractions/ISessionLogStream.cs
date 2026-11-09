using NexusRouter.Domain.Models;

namespace NexusRouter.Domain.Abstractions;

/// <summary>
/// Append-only log of one ingress session. One writer.
/// </summary>
public interface ISessionLogStream
{
    /// <summary>
    /// Stable id used in <see cref="LogRef"/>.
    /// </summary>
    ushort StreamId { get; }

    /// <summary>
    /// Current epoch. A sequence reset or a new trading day can start another one.
    /// </summary>
    int Epoch { get; }

    /// <summary>
    /// Last ingress sequence that readers may see. Published with <see cref="Volatile.Read(ref long)"/>.
    /// </summary>
    long Committed { get; }

    /// <summary>
    /// How many times the writer had to create the next segment itself.
    /// </summary>
    long SyncRotationCount { get; }

    /// <summary>
    /// True after an I/O failure. Later appends fail until the process restarts.
    /// </summary>
    bool IsDegraded { get; }

    /// <summary>
    /// Copies the original frame into the mapped segment and publishes the index slot.
    /// </summary>
    /// <param name="frame">Frame as the counterpart sent it.</param>
    /// <param name="inSeq">Ingress sequence (tag 34).</param>
    /// <param name="ingressTs">Ingress timestamp stored with the record.</param>
    /// <returns>The location, or <see cref="AppendResult.Failed"/>.</returns>
    /// <remarks>Zero allocation on the success path. Does not throw. Safe to call on the hot path.</remarks>
    AppendResult Append(ReadOnlySpan<byte> frame, long inSeq, long ingressTs);

    /// <summary>
    /// Starts a new epoch. The next append opens a new segment.
    /// </summary>
    /// <param name="epoch">Epoch number. Must be greater than the current one.</param>
    /// <param name="baseSeq">First ingress sequence of the new epoch.</param>
    void BeginEpoch(int epoch, long baseSeq);
}
