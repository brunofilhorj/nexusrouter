using System.ComponentModel.DataAnnotations;

namespace DropCopySim.Core.Acceptor;

/// <summary>
/// Optional TLS settings for the FIX acceptor listener.
/// Certificate password is never stored here — only the env-var name is referenced.
/// </summary>
public sealed record TlsOptions : IValidatableObject
{
    /// <summary>When <c>true</c>, the acceptor wraps accepted sockets with TLS.</summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Path to the server certificate (PFX/PEM) used when <see cref="Enabled"/> is <c>true</c>.
    /// </summary>
    public string CertificatePath { get; init; } = string.Empty;

    /// <summary>
    /// Name of the environment variable that holds the certificate password.
    /// Never the password value itself (coding-standards §9).
    /// </summary>
    public string CertificatePasswordEnvVar { get; init; } = "SIM_CERT_PWD";

    /// <inheritdoc />
    public IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        if (Enabled && string.IsNullOrWhiteSpace(CertificatePath))
        {
            yield return new ValidationResult(
                "CertificatePath is required when TLS is enabled.",
                [nameof(CertificatePath)]);
        }
    }
}
