namespace NexusRouter.Egress.Fix;

/// <summary>
/// Destino FIX de um dispatcher outbound. Credencial de TLS, quando existe, é só o nome da variável.
/// </summary>
public sealed class FixOutboundDispatcherOptions
{
    /// <summary>
    /// Host do cliente downstream.
    /// </summary>
    public required string Host { get; init; }

    /// <summary>
    /// Porta TCP do cliente downstream.
    /// </summary>
    public required int Port { get; init; }

    /// <summary>
    /// SenderCompID (tag 49) desta sessão outbound.
    /// </summary>
    public required string SenderCompId { get; init; }

    /// <summary>
    /// TargetCompID (tag 56) do cliente downstream.
    /// </summary>
    public required string TargetCompId { get; init; }

    /// <summary>
    /// BeginString (tag 8), por exemplo <c>FIX.4.4</c>.
    /// </summary>
    public required string BeginString { get; init; }

    /// <summary>
    /// Diretório do arquivo de MsgSeqNum deste dispatcher.
    /// </summary>
    public required string SequenceStoreDirectory { get; init; }

    /// <summary>
    /// HeartBtInt (tag 108), em segundos.
    /// </summary>
    public int HeartBtInt { get; init; } = 20;

    /// <summary>
    /// Tempo máximo de espera pela resposta de Logon.
    /// </summary>
    public TimeSpan LogonTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Capacidade do channel de entrada deste dispatcher.
    /// </summary>
    public int ChannelCapacity { get; init; } = 8192;

    /// <summary>
    /// Quando <see langword="true"/>, o socket passa por TLS antes do Logon.
    /// </summary>
    public bool TlsEnabled { get; init; }

    /// <summary>
    /// Caminho do certificado cliente PKCS#12. Nulo quando o TLS não usa certificado de cliente.
    /// </summary>
    public string? TlsCertificatePath { get; init; }

    /// <summary>
    /// Nome da variável com a senha do PKCS#12. Nulo quando o arquivo não tem senha.
    /// </summary>
    public string? TlsCertificatePasswordEnvVar { get; init; }

    /// <summary>
    /// Atraso base da reconexão.
    /// </summary>
    public TimeSpan ReconnectBaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Teto do atraso de reconexão, aplicado antes do jitter.
    /// </summary>
    public TimeSpan ReconnectMaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Fração de jitter da política de reconexão.
    /// </summary>
    public double ReconnectJitterFactor { get; init; } = 0.2;

    /// <summary>
    /// Rejeita host, porta, comp IDs ou tempos inválidos.
    /// </summary>
    /// <exception cref="ArgumentException">Host, BeginString, comp ID ou diretório é inválido.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Porta, HeartBtInt, timeout ou atraso de reconexão é inválido.</exception>
    public void EnsureValid()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(Host);
        ArgumentException.ThrowIfNullOrWhiteSpace(SenderCompId);
        ArgumentException.ThrowIfNullOrWhiteSpace(TargetCompId);
        ArgumentException.ThrowIfNullOrWhiteSpace(BeginString);
        ArgumentException.ThrowIfNullOrWhiteSpace(SequenceStoreDirectory);
        if (Port is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(Port), Port, "A porta deve estar entre 1 e 65535.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(HeartBtInt);
        if (LogonTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(LogonTimeout), LogonTimeout, "O timeout de Logon deve ser positivo.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ChannelCapacity);
        if (ReconnectBaseDelay < TimeSpan.Zero || ReconnectMaxDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(ReconnectBaseDelay), "O atraso de reconexão não pode ser negativo.");
        }

        if (!double.IsFinite(ReconnectJitterFactor) || ReconnectJitterFactor < 0d)
        {
            throw new ArgumentOutOfRangeException(nameof(ReconnectJitterFactor), ReconnectJitterFactor, "O jitter deve ser finito e não negativo.");
        }
    }
}
