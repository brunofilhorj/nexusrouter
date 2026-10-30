namespace NexusRouter.Ingress.Sessions;

/// <summary>
/// Parâmetros de conexão de uma sessão FIX.
/// </summary>
/// <remarks>
/// Credenciais de TLS não entram aqui. <see cref="TlsCertificatePasswordEnvVar"/> guarda só o nome da variável de ambiente.
/// </remarks>
public sealed class SessionOptions
{
    /// <summary>
    /// Identificador lógico da sessão.
    /// </summary>
    public required string SessionId { get; init; }

    /// <summary>
    /// Host ou endereço do gateway.
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// Porta TCP do gateway.
    /// </summary>
    public required int Port { get; init; }

    /// <summary>
    /// SenderCompID (tag 49) usado no Logout de encerramento.
    /// </summary>
    public required string SenderCompId { get; init; }

    /// <summary>
    /// TargetCompID (tag 56) usado no Logout de encerramento.
    /// </summary>
    public required string TargetCompId { get; init; }

    /// <summary>
    /// Valor da tag 8, sem o prefixo <c>8=</c>. Exemplos: <c>FIX.4.4</c>, <c>FIX.5.0</c>, <c>FIXT.1.1</c>.
    /// </summary>
    public required string BeginString { get; init; }

    /// <summary>
    /// HeartBtInt (tag 108), em segundos. No B3 EntryPoint de produção o valor é 20.
    /// </summary>
    public int HeartBtInt { get; init; } = 20;

    /// <summary>
    /// Heartbeats consecutivos ausentes antes de enviar TestRequest.
    /// </summary>
    public int MissedHeartbeatThreshold { get; init; } = 2;

    /// <summary>
    /// Tempo máximo de espera pela resposta de Logon depois do envio.
    /// </summary>
    public TimeSpan LogonTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Quando <see langword="true"/>, o socket é encapsulado em TLS antes do pipe.
    /// </summary>
    public bool TlsEnabled { get; init; }

    /// <summary>
    /// Caminho do certificado cliente PKCS#12. Nulo quando o TLS não usa certificado de cliente.
    /// </summary>
    public string? TlsCertificatePath { get; init; }

    /// <summary>
    /// Nome da variável de ambiente com a senha do PKCS#12. Nulo quando o arquivo não tem senha.
    /// </summary>
    public string? TlsCertificatePasswordEnvVar { get; init; }
}
