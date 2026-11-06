using System.ComponentModel.DataAnnotations;

namespace DropCopySim.Core.Acceptor;

/// <summary>
/// FIX 4.4 acceptor listen and session identity options.
/// Bound from configuration section <c>DropCopySim:Acceptor</c>.
/// </summary>
public sealed record AcceptorOptions : IValidatableObject
{
    /// <summary>Configuration section name under <c>DropCopySim</c>.</summary>
    public const string SectionName = "Acceptor";

    /// <summary>IP address or hostname to bind the TCP listener to.</summary>
    [Required]
    [MinLength(1)]
    public string ListenAddress { get; init; } = "0.0.0.0";

    /// <summary>TCP port the FIX acceptor listens on (local default: 9876).</summary>
    [Range(1, 65535)]
    public int ListenPort { get; init; } = 9876;

    /// <summary>FIX BeginString; B3 EntryPoint uses <c>FIX.4.4</c>.</summary>
    [Required]
    public string BeginString { get; init; } = "FIX.4.4";

    /// <summary>
    /// CompID of the simulated B3 side (SenderCompID on outbound messages).
    /// </summary>
    [Required]
    [MinLength(1)]
    public string SenderCompId { get; init; } = "ENTRYPOINT-FIRM-0001";

    /// <summary>
    /// Expected CompID of the connecting client (NexusRouter SenderCompID).
    /// </summary>
    [Required]
    [MinLength(1)]
    public string TargetCompId { get; init; } = "CLIENT1";

    /// <summary>Negotiated heartbeat interval in seconds (FIX tag 108).</summary>
    [Range(1, 300)]
    public int HeartBtIntSeconds { get; init; } = 20;

    /// <summary>How the acceptor answers the next Logon.</summary>
    public LogonMode LogonMode { get; init; } = LogonMode.Success;

    /// <summary>Optional TLS wrapper for the acceptor socket.</summary>
    [Required]
    public TlsOptions Tls { get; init; } = new();

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (!string.Equals(BeginString, "FIX.4.4", StringComparison.Ordinal))
        {
            yield return new ValidationResult(
                "BeginString must be FIX.4.4 for the B3 EntryPoint drop-copy simulator.",
                [nameof(BeginString)]);
        }

        var tlsContext = new ValidationContext(Tls);
        var tlsResults = new List<ValidationResult>();
        if (!Validator.TryValidateObject(Tls, tlsContext, tlsResults, validateAllProperties: true))
        {
            foreach (var result in tlsResults)
            {
                yield return result;
            }
        }
    }
}
