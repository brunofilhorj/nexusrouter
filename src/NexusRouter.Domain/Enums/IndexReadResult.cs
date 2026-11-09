namespace NexusRouter.Domain.Enums;

/// <summary>
/// What a client-index read found.
/// </summary>
public enum IndexReadResult
{
    /// <summary>
    /// The slot is published. <c>kind</c> is not zero.
    /// </summary>
    Published = 0,

    /// <summary>
    /// The sequence was reserved and the slot is still unpublished.
    /// </summary>
    NotYet = 1,

    /// <summary>
    /// No slot exists for this sequence.
    /// </summary>
    Missing = 2,
}
