namespace NexusRouter.Domain.Models;

/// <summary>
/// One published slot in a client index.
/// </summary>
/// <param name="Kind">1 for an application record, 2 for an administrative sequence.</param>
/// <param name="Flags">Reserved. Writers store 0.</param>
/// <param name="Stream">Ingress stream id. Zero for an administrative slot.</param>
/// <param name="Segment">Segment number inside the stream day and epoch.</param>
/// <param name="Offset">Byte offset of the record in the segment log.</param>
public readonly record struct IndexEntry(byte Kind, byte Flags, ushort Stream, uint Segment, ulong Offset);
