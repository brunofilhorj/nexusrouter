using System.Buffers;

namespace NexusRouter.Domain.Abstractions;

/// <summary>
/// Reads committed ingress records by sequence.
/// </summary>
public interface ISessionLogReader
{
    /// <summary>
    /// Copies one committed record into <paramref name="destination"/>.
    /// </summary>
    /// <param name="stream">Ingress stream id.</param>
    /// <param name="epoch">Epoch of the record.</param>
    /// <param name="inSeq">Ingress sequence.</param>
    /// <param name="destination">Receives the original frame.</param>
    /// <returns>False when the sequence is past <c>committed</c>, missing, or still torn.</returns>
    bool TryRead(ushort stream, int epoch, long inSeq, IBufferWriter<byte> destination);

    /// <summary>
    /// Copies the record a client-index slot points at.
    /// </summary>
    /// <param name="stream">Ingress stream id of the slot.</param>
    /// <param name="segment">Segment number stored in the slot.</param>
    /// <param name="offset">Byte offset stored in the slot.</param>
    /// <param name="destination">Receives the original frame.</param>
    /// <returns>False when the record is missing, torn or fails its checksum.</returns>
    bool TryReadAt(ushort stream, uint segment, ulong offset, IBufferWriter<byte> destination);

    /// <summary>
    /// Last readable ingress sequence of one epoch. Zero when that epoch has no record.
    /// </summary>
    /// <param name="stream">Ingress stream id.</param>
    /// <param name="epoch">Epoch to query.</param>
    /// <returns>The committed sequence, or zero.</returns>
    long GetCommitted(ushort stream, int epoch);
}
