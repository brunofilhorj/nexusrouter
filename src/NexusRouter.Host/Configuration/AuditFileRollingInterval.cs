namespace NexusRouter.Host.Configuration;

/// <summary>
/// Quando o arquivo de auditoria do Serilog vira. Os nomes acompanham <c>RollingInterval</c>.
/// </summary>
public enum AuditFileRollingInterval
{
    /// <summary>
    /// Um único arquivo, sem rollover.
    /// </summary>
    Infinite = 0,

    /// <summary>
    /// Um arquivo por ano.
    /// </summary>
    Year = 1,

    /// <summary>
    /// Um arquivo por mês.
    /// </summary>
    Month = 2,

    /// <summary>
    /// Um arquivo por dia.
    /// </summary>
    Day = 3,

    /// <summary>
    /// Um arquivo por hora. É o valor do exemplo de configuração.
    /// </summary>
    Hour = 4,

    /// <summary>
    /// Um arquivo por minuto.
    /// </summary>
    Minute = 5,
}
