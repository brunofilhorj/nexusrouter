using Microsoft.Extensions.Options;

namespace NexusRouter.Host.Configuration;

/// <summary>
/// Acceptor FIX. O NexusRouter escuta; os clientes conectam aqui.
/// </summary>
/// <remarks>A senha do certificado, quando existe, fica só em <see cref="TlsOptions.CertificatePasswordEnvVar"/>.</remarks>
public sealed class FixOutboundDispatcherOptions : DispatcherOptions, IValidateOptions<FixOutboundDispatcherOptions>
{
    /// <inheritdoc />
    public override DispatcherType Type => DispatcherType.FixOutbound;

    /// <summary>
    /// Endereço IP em que o acceptor escuta. Exemplo: <c>0.0.0.0</c> ou <c>127.0.0.1</c>.
    /// </summary>
    public required string ListenAddress { get; init; }

    /// <summary>
    /// Porta TCP em que o acceptor escuta.
    /// </summary>
    public int ListenPort { get; init; }

    /// <summary>
    /// SenderCompID (tag 49) das mensagens que o NexusRouter envia.
    /// </summary>
    public required string SenderCompId { get; init; }

    /// <summary>
    /// BeginString (tag 8), por exemplo <c>FIX.4.4</c>.
    /// </summary>
    public required string BeginString { get; init; }

    /// <summary>
    /// SenderCompID aceitos no Logon do cliente. Lista vazia aceita qualquer um cujo TargetCompID seja <see cref="SenderCompId"/>.
    /// </summary>
    public List<string> AllowedSenderCompIds { get; init; } = [];

    /// <summary>
    /// HeartBtInt (tag 108) anunciado no Logon de resposta, em segundos.
    /// </summary>
    public int HeartBtInt { get; init; } = 20;

    /// <summary>
    /// Tempo máximo, em segundos, para o primeiro Logon depois do TCP aceito.
    /// </summary>
    public int LogonTimeoutSeconds { get; init; } = 10;

    /// <summary>
    /// Capacidade do channel do dispatcher e da fila de cada sessão aceita.
    /// </summary>
    public int ChannelCapacity { get; init; } = 8192;

    /// <summary>
    /// TLS do socket aceito. Desligado por omissão.
    /// </summary>
    public TlsOptions Tls { get; init; } = new();

    /// <summary>
    /// Quando <see langword="true"/>, o handshake exige certificado do cliente.
    /// </summary>
    public bool RequireClientCertificate { get; init; }

    /// <summary>
    /// Atraso base, em milissegundos, antes de o loop de accept tentar de novo.
    /// </summary>
    public int AcceptRetryBaseDelayMs { get; init; } = 500;

    /// <summary>
    /// Teto do atraso de retry do accept, em milissegundos, aplicado antes do jitter.
    /// </summary>
    public int AcceptRetryMaxDelayMs { get; init; } = 30_000;

    /// <summary>
    /// Fração de jitter do retry do accept.
    /// </summary>
    public double AcceptRetryJitterFactor { get; init; } = 0.2;

    /// <inheritdoc />
    public override ValidateOptionsResult Validate(string? name) => Validate(name, this);

    /// <summary>
    /// Confere endereço, comp IDs, tempos e TLS de servidor.
    /// </summary>
    /// <param name="name">Nome das options no container. Pode ser nulo neste estágio.</param>
    /// <param name="options">Dispatcher já lido da configuração.</param>
    /// <returns>Sucesso ou a lista de campos que impedem a subida.</returns>
    public ValidateOptionsResult Validate(string? name, FixOutboundDispatcherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];
        AddIdentity(options, failures);
        ConfigurationChecks.Add(failures, ConfigurationChecks.Ip(options.ListenAddress, nameof(options.ListenAddress)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Port(options.ListenPort, nameof(options.ListenPort)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.FixToken(options.SenderCompId, nameof(options.SenderCompId), required: true));
        ConfigurationChecks.Add(failures, ConfigurationChecks.FixToken(options.BeginString, nameof(options.BeginString), required: true));
        AddAllowedSenders(options.AllowedSenderCompIds, failures);
        ConfigurationChecks.Add(failures, ConfigurationChecks.Positive(options.HeartBtInt, nameof(options.HeartBtInt)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Positive(options.LogonTimeoutSeconds, nameof(options.LogonTimeoutSeconds)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Positive(options.ChannelCapacity, nameof(options.ChannelCapacity)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.NonNegative(options.AcceptRetryBaseDelayMs, nameof(options.AcceptRetryBaseDelayMs)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.NonNegative(options.AcceptRetryMaxDelayMs, nameof(options.AcceptRetryMaxDelayMs)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Jitter(options.AcceptRetryJitterFactor, nameof(options.AcceptRetryJitterFactor)));
        ConfigurationChecks.AppendTls(failures, options.Tls, requireCertificateWhenEnabled: true);
        if (options.RequireClientCertificate && options.Tls is not null && !options.Tls.Enabled)
        {
            failures.Add("RequireClientCertificate exige Tls.Enabled true.");
        }

        return ConfigurationChecks.From(failures);
    }

    private static void AddAllowedSenders(List<string>? senders, List<string> failures)
    {
        if (senders is null)
        {
            failures.Add("AllowedSenderCompIds é obrigatório.");
            return;
        }

        for (int i = 0; i < senders.Count; i++)
        {
            ConfigurationChecks.Add(
                failures,
                ConfigurationChecks.FixToken(senders[i], $"AllowedSenderCompIds[{i}]", required: true));
        }
    }
}
