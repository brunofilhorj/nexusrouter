namespace NexusRouter.Audit;

/// <summary>
/// Capacidade, overflow e lote do cold path de auditoria.
/// </summary>
/// <remarks>
/// Credencial de banco não entra aqui. O sink PostgreSQL lê usuário e senha de variáveis de ambiente.
/// </remarks>
public sealed class AuditOptions
{
    /// <summary>
    /// Quantidade máxima de <see cref="NexusRouter.Domain.Models.AuditRecord"/> no channel.
    /// </summary>
    public int ChannelCapacity { get; init; } = 65536;

    /// <summary>
    /// Comportamento quando o channel está cheio.
    /// </summary>
    public AuditOverflowMode OverflowMode { get; init; } = AuditOverflowMode.DropOldest;

    /// <summary>
    /// Registros acumulados antes de um flush forçado.
    /// </summary>
    public int BatchSize { get; init; } = 1000;

    /// <summary>
    /// Tempo máximo, em milissegundos, que um lote incompleto espera antes do flush.
    /// </summary>
    public int FlushIntervalMs { get; init; } = 100;

    /// <summary>
    /// Política do payload bruto na hypertable.
    /// </summary>
    public AuditPersistMode PersistMode { get; init; } = AuditPersistMode.Full;

    /// <summary>
    /// Fração de mensagens com <c>raw_fix</c> quando <see cref="PersistMode"/> é <see cref="AuditPersistMode.Sampled"/>.
    /// <c>1</c> guarda todas; <c>0</c> não guarda nenhuma. Fora de <see cref="AuditPersistMode.Sampled"/> o valor é ignorado.
    /// </summary>
    public double SampleRate { get; init; } = 1.0;

    /// <summary>
    /// Rejeita capacidade, lote ou taxa que o writer não consegue aplicar.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Algum limite é menor ou igual a zero, ou a taxa está fora de 0 a 1.</exception>
    public void EnsureValid()
    {
        if (ChannelCapacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ChannelCapacity), ChannelCapacity, "A capacidade do channel precisa ser maior que zero.");
        }

        if (BatchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(BatchSize), BatchSize, "O lote precisa ter pelo menos um registro.");
        }

        if (FlushIntervalMs <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(FlushIntervalMs), FlushIntervalMs, "O intervalo de flush precisa ser maior que zero.");
        }

        if (!Enum.IsDefined(OverflowMode))
        {
            throw new ArgumentOutOfRangeException(nameof(OverflowMode), OverflowMode, "Modo de overflow desconhecido.");
        }

        if (!Enum.IsDefined(PersistMode))
        {
            throw new ArgumentOutOfRangeException(nameof(PersistMode), PersistMode, "Modo de persistência desconhecido.");
        }

        if (double.IsNaN(SampleRate) || SampleRate < 0 || SampleRate > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(SampleRate), SampleRate, "A taxa de amostra fica entre 0 e 1.");
        }
    }
}
