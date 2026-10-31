using System.Net;

namespace NexusRouter.Egress.Fix;

/// <summary>
/// Acceptor FIX de um dispatcher. O NexusRouter escuta; os clientes conectam aqui.
/// </summary>
/// <remarks>Credencial de TLS, quando existe, é só o nome da variável.</remarks>
public sealed class FixOutboundDispatcherOptions
{
    /// <summary>
    /// Endereço IP em que o acceptor escuta. Exemplo: <c>0.0.0.0</c> ou <c>127.0.0.1</c>.
    /// </summary>
    public required string ListenAddress { get; init; }

    /// <summary>
    /// Porta TCP em que o acceptor escuta.
    /// </summary>
    public required int ListenPort { get; init; }

    /// <summary>
    /// SenderCompID (tag 49) das mensagens que o NexusRouter envia. O Logon do cliente traz este valor na tag 56.
    /// </summary>
    public required string SenderCompId { get; init; }

    /// <summary>
    /// BeginString (tag 8), por exemplo <c>FIX.4.4</c>.
    /// </summary>
    public required string BeginString { get; init; }

    /// <summary>
    /// Diretório dos arquivos de MsgSeqNum. Um arquivo por sessão aceita.
    /// </summary>
    public required string SequenceStoreDirectory { get; init; }

    /// <summary>
    /// SenderCompID (tag 49) aceitos no Logon do cliente. Lista vazia aceita qualquer um cujo TargetCompID seja <see cref="SenderCompId"/>.
    /// </summary>
    public IReadOnlyList<string> AllowedSenderCompIds { get; init; } = [];

    /// <summary>
    /// HeartBtInt (tag 108) anunciado no Logon de resposta, em segundos.
    /// </summary>
    public int HeartBtInt { get; init; } = 20;

    /// <summary>
    /// Tempo máximo para o primeiro Logon depois do TCP aceito.
    /// </summary>
    public TimeSpan LogonTimeout { get; init; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Capacidade do channel do dispatcher e da fila de cada sessão aceita.
    /// </summary>
    public int ChannelCapacity { get; init; } = 8192;

    /// <summary>
    /// Quando <see langword="true"/>, o socket aceito passa por TLS antes do Logon.
    /// </summary>
    public bool TlsEnabled { get; init; }

    /// <summary>
    /// Caminho do certificado servidor PKCS#12. Obrigatório quando <see cref="TlsEnabled"/> é <see langword="true"/>.
    /// </summary>
    public string? TlsCertificatePath { get; init; }

    /// <summary>
    /// Nome da variável com a senha do PKCS#12. Nulo quando o arquivo não tem senha.
    /// </summary>
    public string? TlsCertificatePasswordEnvVar { get; init; }

    /// <summary>
    /// Quando <see langword="true"/> e o TLS está ligado, o handshake exige certificado do cliente.
    /// </summary>
    public bool TlsRequireClientCertificate { get; init; }

    /// <summary>
    /// Atraso base antes de o loop de accept tentar de novo depois de um erro de socket.
    /// </summary>
    public TimeSpan AcceptRetryBaseDelay { get; init; } = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// Teto do atraso de retry do accept, aplicado antes do jitter.
    /// </summary>
    public TimeSpan AcceptRetryMaxDelay { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Fração de jitter do retry do accept.
    /// </summary>
    public double AcceptRetryJitterFactor { get; init; } = 0.2;

    /// <summary>
    /// Rejeita endereço, porta, comp ID ou tempos inválidos.
    /// </summary>
    /// <exception cref="ArgumentException">Endereço, BeginString, comp ID ou diretório é inválido.</exception>
    /// <exception cref="ArgumentNullException"><see cref="AllowedSenderCompIds"/> é nulo.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Porta, HeartBtInt, timeout ou atraso de retry é inválido.</exception>
    /// <exception cref="InvalidOperationException">TLS está ligado sem certificado de servidor.</exception>
    public void EnsureValid()
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ListenAddress);
        if (!IPAddress.TryParse(ListenAddress, out _))
        {
            throw new ArgumentException("ListenAddress deve ser um endereço IP.", nameof(ListenAddress));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(SenderCompId);
        ArgumentException.ThrowIfNullOrWhiteSpace(BeginString);
        ArgumentException.ThrowIfNullOrWhiteSpace(SequenceStoreDirectory);
        ValidateToken(SenderCompId, nameof(SenderCompId));
        ValidateToken(BeginString, nameof(BeginString));
        ArgumentNullException.ThrowIfNull(AllowedSenderCompIds);
        foreach (string sender in AllowedSenderCompIds)
        {
            ValidateToken(sender, nameof(AllowedSenderCompIds));
        }

        if (ListenPort is < 1 or > 65535)
        {
            throw new ArgumentOutOfRangeException(nameof(ListenPort), ListenPort, "A porta deve estar entre 1 e 65535.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(HeartBtInt);
        if (LogonTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(LogonTimeout), LogonTimeout, "O timeout de Logon deve ser positivo.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(ChannelCapacity);
        if (AcceptRetryBaseDelay < TimeSpan.Zero || AcceptRetryMaxDelay < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(AcceptRetryBaseDelay), "O atraso de retry do accept não pode ser negativo.");
        }

        if (!double.IsFinite(AcceptRetryJitterFactor) || AcceptRetryJitterFactor < 0d)
        {
            throw new ArgumentOutOfRangeException(
                nameof(AcceptRetryJitterFactor),
                AcceptRetryJitterFactor,
                "O jitter deve ser finito e não negativo.");
        }

        if (TlsEnabled && string.IsNullOrWhiteSpace(TlsCertificatePath))
        {
            throw new InvalidOperationException("O TLS do acceptor FIX exige TlsCertificatePath.");
        }
    }

    private static void ValidateToken(string value, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        foreach (char ch in value)
        {
            if (ch > 127 || ch == '=' || ch == '\u0001')
            {
                throw new ArgumentException("O valor deve ser ASCII e não pode conter SOH nem '='.", name);
            }
        }
    }
}
