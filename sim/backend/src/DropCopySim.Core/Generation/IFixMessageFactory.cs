namespace DropCopySim.Core.Generation;

/// <summary>
/// Builds a FIX application message from a resolved order context.
/// </summary>
public interface IFixMessageFactory
{
    /// <summary>
    /// Gets the stable name of this message factory.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Writes a complete FIX frame for <paramref name="ctx"/> into the caller-owned buffer.
    /// </summary>
    /// <param name="destination">Destination buffer for the complete FIX frame.</param>
    /// <param name="ctx">Resolved order event to encode.</param>
    /// <param name="seqNum">Session-level message sequence number.</param>
    /// <param name="businessSeqNum">Simulator-level business sequence number.</param>
    /// <returns>The number of bytes written to <paramref name="destination"/>.</returns>
    int Build(Span<byte> destination, in OrderContext ctx, long seqNum, long businessSeqNum);
}
