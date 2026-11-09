using Microsoft.Extensions.Options;

namespace NexusRouter.Host.Configuration;

/// <summary>
/// Acceptor WebSocket. O NexusRouter escuta; clientes web ou nativos conectam aqui.
/// </summary>
/// <remarks>
/// A autenticação viaja no primeiro frame (Logon JSON), nunca na URL. O token não fica nesta classe.
/// </remarks>
public sealed class WebSocketDispatcherOptions : DispatcherOptions, IValidateOptions<WebSocketDispatcherOptions>
{
    /// <inheritdoc />
    public override DispatcherType Type => DispatcherType.WebSocket;

    /// <summary>
    /// Endereço IP em que o acceptor WebSocket escuta. Exemplo: <c>0.0.0.0</c> ou <c>127.0.0.1</c>.
    /// </summary>
    public required string ListenAddress { get; init; }

    /// <summary>
    /// Porta TCP em que o acceptor WebSocket escuta.
    /// </summary>
    public int ListenPort { get; init; }

    /// <summary>
    /// Origens aceitas no header <c>Origin</c>. Lista vazia recusa qualquer origem.
    /// </summary>
    public List<string> AllowedOrigins { get; init; } = [];

    /// <summary>
    /// Intervalo de heartbeat do WebSocket, em segundos.
    /// </summary>
    public int HeartbeatIntervalSeconds { get; init; } = 30;

    /// <summary>
    /// Limite de heartbeats perdidos antes de derrubar a sessão.
    /// </summary>
    public int MissedHeartbeatLimit { get; init; } = 2;

    /// <summary>
    /// Tempo limite para receber o Logon JSON após o upgrade HTTP, em segundos.
    /// </summary>
    public int LogonTimeoutSeconds { get; init; } = 5;

    /// <summary>
    /// Tamanho máximo, em bytes, de um frame de controle (Logon/Heartbeat).
    /// </summary>
    public int MaxControlFrameBytes { get; init; } = 4096;

    /// <summary>
    /// Quantidade máxima de campos projetados em uma mensagem.
    /// </summary>
    public int MaxFields { get; init; } = 64;

    /// <summary>
    /// Tamanho máximo, em bytes, de um frame outbound projetado.
    /// </summary>
    public int MaxOutboundFrameBytes { get; init; } = 65536;

    /// <summary>
    /// Capacidade do channel bounded de cada sessão WebSocket conectada.
    /// </summary>
    public int SessionChannelCapacity { get; init; } = 1024;

    /// <summary>
    /// Capacidade do channel de entrada do dispatcher.
    /// </summary>
    public int ChannelCapacity { get; init; } = 8192;

    /// <summary>
    /// TLS de servidor (wss). Desligado por omissão.
    /// </summary>
    public TlsOptions Tls { get; init; } = new();

    /// <inheritdoc />
    public override ValidateOptionsResult Validate(string? name) => Validate(name, this);

    /// <summary>
    /// Confere endereço, origens, tempos, limites de frame e TLS de servidor.
    /// </summary>
    /// <param name="name">Nome das options no container. Pode ser nulo neste estágio.</param>
    /// <param name="options">Dispatcher já lido da configuração.</param>
    /// <returns>Sucesso ou a lista de campos que impedem a subida.</returns>
    public ValidateOptionsResult Validate(string? name, WebSocketDispatcherOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];
        AddIdentity(options, failures);
        ConfigurationChecks.Add(failures, ConfigurationChecks.Ip(options.ListenAddress, nameof(options.ListenAddress)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Port(options.ListenPort, nameof(options.ListenPort)));
        AddAllowedOrigins(options.AllowedOrigins, failures);
        ConfigurationChecks.Add(
            failures,
            ConfigurationChecks.Positive(options.HeartbeatIntervalSeconds, nameof(options.HeartbeatIntervalSeconds)));
        ConfigurationChecks.Add(
            failures,
            ConfigurationChecks.Positive(options.MissedHeartbeatLimit, nameof(options.MissedHeartbeatLimit)));
        ConfigurationChecks.Add(
            failures,
            ConfigurationChecks.Positive(options.LogonTimeoutSeconds, nameof(options.LogonTimeoutSeconds)));
        ConfigurationChecks.Add(
            failures,
            ConfigurationChecks.Positive(options.MaxControlFrameBytes, nameof(options.MaxControlFrameBytes)));
        ConfigurationChecks.Add(
            failures,
            ConfigurationChecks.Positive(options.MaxFields, nameof(options.MaxFields)));
        ConfigurationChecks.Add(
            failures,
            ConfigurationChecks.Positive(options.MaxOutboundFrameBytes, nameof(options.MaxOutboundFrameBytes)));
        ConfigurationChecks.Add(
            failures,
            ConfigurationChecks.Positive(options.SessionChannelCapacity, nameof(options.SessionChannelCapacity)));
        ConfigurationChecks.Add(
            failures,
            ConfigurationChecks.Positive(options.ChannelCapacity, nameof(options.ChannelCapacity)));
        ConfigurationChecks.AppendTls(failures, options.Tls, requireCertificateWhenEnabled: true);
        return ConfigurationChecks.From(failures);
    }

    private static void AddAllowedOrigins(List<string>? origins, List<string> failures)
    {
        if (origins is null)
        {
            failures.Add("AllowedOrigins é obrigatório.");
            return;
        }

        for (int i = 0; i < origins.Count; i++)
        {
            ConfigurationChecks.Add(
                failures,
                ConfigurationChecks.Origin(origins[i], $"AllowedOrigins[{i}]"));
        }
    }
}
