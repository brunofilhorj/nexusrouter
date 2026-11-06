namespace DropCopySim.Host.Endpoints;

/// <summary>
/// Structured error body returned by every control-API endpoint on failure (FR-API-05).
/// </summary>
/// <remarks>
/// Serialized as JSON <c>{ "code": "...", "message": "..." }</c>. The <see cref="Code"/> is a stable,
/// machine-readable token (e.g. <c>invalid_state</c>, <c>validation_failed</c>, <c>not_found</c>) and
/// <see cref="Message"/> is a human-readable explanation safe to surface in the SimPanel.
/// </remarks>
public sealed record ApiError
{
    /// <summary>Creates a structured error body.</summary>
    /// <param name="code">Stable machine-readable error token.</param>
    /// <param name="message">Human-readable explanation.</param>
    public ApiError(string code, string message)
    {
        Code = code;
        Message = message;
    }

    /// <summary>Stable machine-readable error token.</summary>
    public string Code { get; }

    /// <summary>Human-readable explanation.</summary>
    public string Message { get; }
}
