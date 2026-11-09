namespace NexusRouter.Domain.Enums;

/// <summary>
/// How often consumer state is written to disk.
/// </summary>
public enum SessionLogFlushMode
{
    /// <summary>
    /// The flusher writes on a timer. The default interval is 5 seconds.
    /// </summary>
    Interval = 0,

    /// <summary>
    /// The flusher writes after each state change. The unclean-restart margin must be 0.
    /// </summary>
    PerMessage = 1,
}
