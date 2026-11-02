using NexusRouter.Domain.Models;

namespace NexusRouter.Audit.Sinks;

/// <summary>
/// Destino de um lote de auditoria. Serilog e PostgreSQL implementam este contrato.
/// </summary>
/// <remarks>
/// O hot path não chama <see cref="IAuditSink"/>. Quem chama é <see cref="AuditWriterService"/>,
/// fora da thread que roteia a mensagem. O lote passado a <see cref="WriteBatchAsync"/>
/// vale só até o método devolver.
/// </remarks>
public interface IAuditSink
{
    /// <summary>
    /// Nome estável do sink, usado no log quando a gravação falha.
    /// </summary>
    string Name { get; }

    /// <summary>
    /// Abre o destino. No PostgreSQL, valida a conexão. Não cria tabela.
    /// </summary>
    /// <param name="cancellationToken">Cancela a abertura.</param>
    /// <returns>Tarefa concluída quando o sink aceita lotes.</returns>
    ValueTask StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Grava um lote. Uma falha aqui não pode impedir os outros sinks do mesmo lote.
    /// </summary>
    /// <param name="batch">Registros já retirados do channel.</param>
    /// <param name="cancellationToken">Cancela esta gravação.</param>
    /// <returns>Tarefa concluída quando o lote foi aceito ou a falha foi registrada.</returns>
    ValueTask WriteBatchAsync(ReadOnlyMemory<AuditRecord> batch, CancellationToken cancellationToken);

    /// <summary>
    /// Fecha o destino depois do último lote.
    /// </summary>
    /// <param name="cancellationToken">Cancela a espera pelo fechamento.</param>
    /// <returns>Tarefa concluída quando o sink soltou a conexão ou o arquivo.</returns>
    ValueTask StopAsync(CancellationToken cancellationToken);
}
