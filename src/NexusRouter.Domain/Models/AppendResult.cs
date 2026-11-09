namespace NexusRouter.Domain.Models;

/// <summary>
/// Result of an append. Failure never throws.
/// </summary>
/// <remarks>Zero allocation. Safe to call on the hot path.</remarks>
public readonly struct AppendResult
{
    private readonly bool _ok;

    private AppendResult(bool ok, LogRef logRef)
    {
        _ok = ok;
        Ref = logRef;
    }

    /// <summary>
    /// True when the record and its index slot were published.
    /// </summary>
    public bool IsOk => _ok;

    /// <summary>
    /// Location of the record when <see cref="IsOk"/> is true.
    /// </summary>
    public LogRef Ref { get; }

    /// <summary>
    /// A published record.
    /// </summary>
    /// <param name="logRef">Location of the record.</param>
    /// <returns>A successful result.</returns>
    public static AppendResult Ok(LogRef logRef) => new(true, logRef);

    /// <summary>
    /// The record was not published. Routing must continue.
    /// </summary>
    public static AppendResult Failed => default;
}
