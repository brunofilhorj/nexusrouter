using NexusRouter.Domain.Models;

using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace NexusRouter.Audit.Sinks;

/// <summary>
/// Publica cada registro do lote como um evento Serilog com propriedades nomeadas.
/// </summary>
/// <remarks>
/// O payload FIX não entra no evento em <see cref="LogEventLevel.Information"/> nem acima.
/// O evento leva só <c>RawFixLength</c>.
/// </remarks>
public sealed class SerilogAuditSink : IAuditSink
{
    private const string Template =
        "Auditoria {SessionId} {IngressTimestampTicks} {MsgType} {ClOrdId} {MsgSeqNum} {DeliveryMode} {@DispatcherIds} {EgressTimestampTicks} {WasDropped} {DropReason} {RawFixLength} {LifecycleState} {LifecycleDetail} {LifecycleTimestamp}";

    private readonly SerilogAuditSinkOptions? _options;
    private readonly ILogger _logger;
    private Logger? _owned;

    /// <summary>
    /// Usa um logger já configurado. O sink não fecha esse logger.
    /// </summary>
    /// <param name="logger">Logger de auditoria.</param>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> é nulo.</exception>
    public SerilogAuditSink(ILogger logger)
    {
        ArgumentNullException.ThrowIfNull(logger);
        _logger = logger;
    }

    /// <summary>
    /// Abre um arquivo rolling só para a auditoria.
    /// </summary>
    /// <param name="options">Caminho e intervalo do arquivo.</param>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> é nulo.</exception>
    /// <exception cref="ArgumentException">O caminho do arquivo está vazio.</exception>
    public SerilogAuditSink(SerilogAuditSinkOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.FilePath);
        _options = options;
        _logger = Logger.None;
    }

    /// <inheritdoc />
    public string Name => "Serilog";

    /// <inheritdoc />
    public ValueTask StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_options is null || _owned is not null)
        {
            return ValueTask.CompletedTask;
        }

        _owned = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .WriteTo.File(_options.FilePath, rollingInterval: _options.RollingInterval)
            .CreateLogger();
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask WriteBatchAsync(ReadOnlyMemory<AuditRecord> batch, CancellationToken cancellationToken)
    {
        ILogger logger = _owned ?? _logger;
        ReadOnlySpan<AuditRecord> records = batch.Span;
        for (int i = 0; i < records.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            AuditRecord record = records[i];
            LogEventLevel level = record.WasDropped ? LogEventLevel.Warning : LogEventLevel.Information;
            SessionLifecycleEvent? lifecycle = record.LifecycleEvent;
            logger.Write(
                level,
                Template,
                record.SessionId.Value,
                record.IngressTimestampTicks,
                record.MsgType,
                record.ClOrdId,
                record.MsgSeqNum,
                record.DeliveryMode,
                record.DispatcherIds,
                record.EgressTimestampTicks,
                record.WasDropped,
                record.DropReason,
                record.RawFix?.Length ?? 0,
                lifecycle?.NewState,
                lifecycle?.Detail,
                lifecycle?.Timestamp);
        }

        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask StopAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Logger? owned = Interlocked.Exchange(ref _owned, null);
        owned?.Dispose();
        return ValueTask.CompletedTask;
    }
}
