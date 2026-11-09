using Microsoft.Extensions.Options;

using NexusRouter.Domain.Enums;

namespace NexusRouter.SessionLog;

/// <summary>
/// File session log. Replaces the old replay channel settings.
/// </summary>
public sealed class SessionLogOptions : IValidateOptions<SessionLogOptions>
{
    /// <summary>
    /// Root directory of streams, clients, brokers, and the clean marker.
    /// </summary>
    public string Directory { get; init; } = "data/sessionlog";

    /// <summary>
    /// Trading-day folders older than this many days are deleted. Default is 5.
    /// </summary>
    public int RetentionTradingDays { get; init; } = 5;

    /// <summary>
    /// When the flusher writes consumer state.
    /// </summary>
    public SessionLogFlushMode FlushMode { get; init; } = SessionLogFlushMode.Interval;

    /// <summary>
    /// Flush period, in milliseconds, used when <see cref="FlushMode"/> is <see cref="SessionLogFlushMode.Interval"/>.
    /// </summary>
    public int FlushIntervalMs { get; init; } = 5000;

    /// <summary>
    /// Fixed size of each pre-allocated <c>.log</c> segment, in megabytes.
    /// </summary>
    public int SegmentSizeMB { get; init; } = 256;

    /// <summary>
    /// Dense index slots in one segment, and slots in one client <c>.cidx</c>.
    /// </summary>
    public int IndexSlotsPerSegment { get; init; } = 1_000_000;

    /// <summary>
    /// Outbound sequence jump after a crash. Must be 0 when flush mode is per message.
    /// </summary>
    public int UncleanRestartSeqMargin { get; init; } = 10_000;

    /// <summary>
    /// Idle wait before a consumer compares its cursor with the committed sequence.
    /// </summary>
    public int CatchUpIdleMs { get; init; } = 200;

    /// <summary>
    /// How long a reserved slot may stay unpublished before a resend treats it as a gap.
    /// </summary>
    public int SlotPublishTimeoutMs { get; init; } = 1000;

    /// <summary>
    /// Checks the directory, sizes, flush mode, and the unclean-restart margin.
    /// </summary>
    /// <param name="name">Options name. May be null.</param>
    /// <param name="options">Bound section.</param>
    /// <returns>Success, or the list of fields that block startup.</returns>
    public ValidateOptionsResult Validate(string? name, SessionLogOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];
        if (string.IsNullOrWhiteSpace(options.Directory))
        {
            failures.Add("Directory is required.");
        }

        if (options.RetentionTradingDays < 1)
        {
            failures.Add("RetentionTradingDays must be at least 1.");
        }

        if (!Enum.IsDefined(options.FlushMode))
        {
            failures.Add("FlushMode is invalid.");
        }

        if (options.FlushMode == SessionLogFlushMode.Interval && options.FlushIntervalMs < 1)
        {
            failures.Add("FlushIntervalMs must be positive when FlushMode is Interval.");
        }

        if (options.SegmentSizeMB < 1)
        {
            failures.Add("SegmentSizeMB must be at least 1.");
        }

        if (options.IndexSlotsPerSegment < 1)
        {
            failures.Add("IndexSlotsPerSegment must be at least 1.");
        }

        if (options.UncleanRestartSeqMargin < 0)
        {
            failures.Add("UncleanRestartSeqMargin cannot be negative.");
        }

        if (options.FlushMode == SessionLogFlushMode.PerMessage && options.UncleanRestartSeqMargin != 0)
        {
            failures.Add("UncleanRestartSeqMargin must be 0 when FlushMode is PerMessage.");
        }

        if (options.CatchUpIdleMs < 1)
        {
            failures.Add("CatchUpIdleMs must be positive.");
        }

        if (options.SlotPublishTimeoutMs < 1)
        {
            failures.Add("SlotPublishTimeoutMs must be positive.");
        }

        return failures.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(failures);
    }
}
