namespace NexusRouter.Audit;

/// <summary>
/// O que o channel de auditoria faz quando está cheio.
/// </summary>
/// <remarks>
/// Os dois modos devolvem o controle na hora. Nenhum espera vaga no channel.
/// </remarks>
public enum AuditOverflowMode
{
    /// <summary>
    /// Descarta o registro mais antigo e aceita o novo.
    /// </summary>
    DropOldest = 0,

    /// <summary>
    /// Descarta o registro novo e mantém os que já estão na fila.
    /// <c>TryWrite</c> ainda devolve <see langword="true"/>: o channel só informa que não bloqueou.
    /// </summary>
    DropWrite = 1,
}
