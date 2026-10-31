namespace NexusRouter.Ingress.Sessions;

/// <summary>
/// Parâmetros de TLS de uma sessão. A senha do certificado não entra aqui.
/// </summary>
public sealed class TlsOptions
{
    /// <summary>
    /// Quando <see langword="false"/>, <see cref="TlsSocketFactory.WrapAsync"/> devolve o stream TCP sem handshake.
    /// </summary>
    public bool Enabled { get; init; }

    /// <summary>
    /// Caminho do certificado cliente PKCS#12. Nulo quando o TLS não usa certificado de cliente.
    /// </summary>
    public string? CertificatePath { get; init; }

    /// <summary>
    /// Nome da variável de ambiente com a senha do PKCS#12. Nulo quando o arquivo não tem senha.
    /// </summary>
    public string? CertificatePasswordEnvVar { get; init; }

    /// <summary>
    /// Nome usado no handshake TLS (SNI e validação do certificado do servidor).
    /// </summary>
    public required string TargetHost { get; init; }
}
