using Serilog;

namespace NexusRouter.Audit;

/// <summary>
/// Arquivo rolling que recebe os eventos de auditoria do Serilog.
/// </summary>
public sealed class SerilogAuditSinkOptions
{
    /// <summary>
    /// Caminho do arquivo. O Serilog acrescenta o sufixo do intervalo quando o rolling não é infinito.
    /// </summary>
    public required string FilePath { get; init; }

    /// <summary>
    /// Quando o arquivo vira. <see cref="RollingInterval.Hour"/> acompanha o exemplo de configuração.
    /// </summary>
    public RollingInterval RollingInterval { get; init; } = RollingInterval.Hour;
}
