using Microsoft.Extensions.Options;

namespace NexusRouter.Host.Configuration;

/// <summary>
/// Channel do cold path de retransmissão. Sem esta seção, os defaults deixam o channel pronto e a retenção continua desligada.
/// </summary>
public sealed class ReplayOptions : IValidateOptions<ReplayOptions>
{
    /// <summary>
    /// Quantidade máxima de registros de replay no channel.
    /// </summary>
    public int ChannelCapacity { get; init; } = 65536;

    /// <summary>
    /// Registros acumulados antes de um flush forçado.
    /// </summary>
    public int BatchSize { get; init; } = 1000;

    /// <summary>
    /// Tempo máximo, em milissegundos, que um lote incompleto espera antes do flush.
    /// </summary>
    public int FlushIntervalMs { get; init; } = 100;

    /// <summary>
    /// Confere capacidade, lote e intervalo.
    /// </summary>
    /// <param name="name">Nome das options no container. Pode ser nulo neste estágio.</param>
    /// <param name="options">Seção Replay já lida da configuração.</param>
    /// <returns>Sucesso ou a lista de campos que impedem a subida.</returns>
    public ValidateOptionsResult Validate(string? name, ReplayOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];
        ConfigurationChecks.Add(failures, ConfigurationChecks.Positive(options.ChannelCapacity, nameof(options.ChannelCapacity)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Positive(options.BatchSize, nameof(options.BatchSize)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Positive(options.FlushIntervalMs, nameof(options.FlushIntervalMs)));
        return ConfigurationChecks.From(failures);
    }
}
