using Microsoft.Extensions.Options;

using NexusRouter.Audit;

namespace NexusRouter.Host.Configuration;

/// <summary>
/// Configuração de auditoria da aplicação: channel, overflow, flush em lote e sinks.
/// </summary>
public sealed class AuditOptions : IValidateOptions<AuditOptions>
{
    /// <summary>
    /// Quantidade máxima de registros de auditoria na fila em memória.
    /// </summary>
    public int ChannelCapacity { get; init; } = 65536;

    /// <summary>
    /// Comportamento quando o channel de auditoria está cheio.
    /// </summary>
    public AuditOverflowMode OverflowMode { get; init; } = AuditOverflowMode.DropOldest;

    /// <summary>
    /// Quantidade de registros acumulados antes do flush forçado aos sinks.
    /// </summary>
    public int BatchSize { get; init; } = 1000;

    /// <summary>
    /// Tempo máximo de espera, em milissegundos, antes de descarregar um lote incompleto.
    /// </summary>
    public int FlushIntervalMs { get; init; } = 100;

    /// <summary>
    /// Modo de persistência do payload FIX bruto na hypertable.
    /// </summary>
    public AuditPersistMode PersistMode { get; init; } = AuditPersistMode.Full;

    /// <summary>
    /// Fração de mensagens com payload bruto gravado quando <see cref="PersistMode"/> é <see cref="AuditPersistMode.Sampled"/>.
    /// </summary>
    public double SampleRate { get; init; } = 1.0;

    /// <summary>
    /// Sinks de destino dos eventos de auditoria (arquivo Serilog, PostgreSQL/TimescaleDB).
    /// </summary>
    public List<AuditSinkOptions> Sinks { get; init; } = [];

    /// <summary>
    /// Confere limites numéricos, enums e valida cada sink configurado.
    /// </summary>
    /// <param name="name">Nome das options no container. Pode ser nulo neste estágio.</param>
    /// <param name="options">Seção Audit já lida da configuração.</param>
    /// <returns>Sucesso ou a lista de campos que impedem a subida.</returns>
    public ValidateOptionsResult Validate(string? name, AuditOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        List<string> failures = [];
        ConfigurationChecks.Add(failures, ConfigurationChecks.Positive(options.ChannelCapacity, nameof(options.ChannelCapacity)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Defined(options.OverflowMode, nameof(options.OverflowMode)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Positive(options.BatchSize, nameof(options.BatchSize)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Positive(options.FlushIntervalMs, nameof(options.FlushIntervalMs)));
        ConfigurationChecks.Add(failures, ConfigurationChecks.Defined(options.PersistMode, nameof(options.PersistMode)));

        if (double.IsNaN(options.SampleRate) || options.SampleRate < 0d || options.SampleRate > 1d)
        {
            failures.Add($"{nameof(options.SampleRate)} deve estar entre 0.0 e 1.0.");
        }

        AddSinks(options.Sinks, failures);
        return ConfigurationChecks.From(failures);
    }

    private static void AddSinks(List<AuditSinkOptions>? sinks, List<string> failures)
    {
        if (sinks is null)
        {
            failures.Add("Sinks é obrigatório.");
            return;
        }

        for (int i = 0; i < sinks.Count; i++)
        {
            AuditSinkOptions? sink = sinks[i];
            if (sink is null)
            {
                failures.Add($"Sinks[{i}] é obrigatório.");
                continue;
            }

            ConfigurationChecks.Append(failures, $"Sinks[{i}]", sink.Validate(null));
        }
    }
}
