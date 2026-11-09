namespace NexusRouter.Domain.Abstractions;

/// <summary>
/// Durable cursor and sequence state of one consumer. The flusher is the only writer of the file.
/// </summary>
public interface IConsumerState
{
    /// <summary>
    /// Next outbound sequence. Owners publish it with a volatile write.
    /// </summary>
    long NextOut { get; }

    /// <summary>
    /// Next ingress sequence expected from the counterpart.
    /// </summary>
    long NextInExpected { get; set; }

    /// <summary>
    /// Current epoch.
    /// </summary>
    long Epoch { get; set; }

    /// <summary>
    /// Next ingress sequence this consumer still has to deliver for <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">Ingress stream id.</param>
    /// <returns>The expected sequence. Zero when unset.</returns>
    long GetExpected(ushort stream);

    /// <summary>
    /// Publishes the next ingress sequence this consumer still has to deliver.
    /// </summary>
    /// <param name="stream">Ingress stream id.</param>
    /// <param name="inSeq">Next sequence.</param>
    void SetExpected(ushort stream, long inSeq);

    /// <summary>
    /// Broker cursor for <paramref name="stream"/>.
    /// </summary>
    /// <param name="stream">Ingress stream id.</param>
    /// <returns>The last contiguous sequence the cursor may advance to.</returns>
    long Cursor(ushort stream);

    /// <summary>
    /// Publishes the broker cursor.
    /// </summary>
    /// <param name="stream">Ingress stream id.</param>
    /// <param name="value">Contiguous cursor.</param>
    void SetCursor(ushort stream, long value);
}
