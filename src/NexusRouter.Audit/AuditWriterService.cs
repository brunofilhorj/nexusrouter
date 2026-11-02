using System.Threading.Channels;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using NexusRouter.Audit.Sinks;
using NexusRouter.Domain.Models;

namespace NexusRouter.Audit;

/// <summary>
/// Lê o channel de auditoria e entrega lotes aos sinks.
/// </summary>
/// <remarks>
/// Única task de leitura. O hot path não chama <see cref="IAuditSink"/>.
/// No shutdown o channel é completado, o que já está na fila é gravado, e a espera tem teto de 10 segundos.
/// </remarks>
public sealed class AuditWriterService : IHostedService
{
    private static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(10);

    private readonly Channel<AuditRecord> _channel;
    private readonly IReadOnlyList<IAuditSink> _sinks;
    private readonly ILogger<AuditWriterService> _logger;
    private readonly AuditOptions _options;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _reader;

    /// <summary>
    /// Recebe o channel, os sinks e os limites de lote.
    /// </summary>
    /// <param name="channel">Channel criado por <see cref="AuditChannelSingleton"/>.</param>
    /// <param name="sinks">Destinos do lote. A falha de um não cancela os outros.</param>
    /// <param name="logger">Log de falha de sink e de drenagem incompleta.</param>
    /// <param name="options">Tamanho do lote e intervalo de flush.</param>
    /// <exception cref="ArgumentNullException">Alguma dependência é nula.</exception>
    public AuditWriterService(
        Channel<AuditRecord> channel,
        IReadOnlyList<IAuditSink> sinks,
        ILogger<AuditWriterService> logger,
        AuditOptions options)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(sinks);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);
        options.EnsureValid();
        _channel = channel;
        _sinks = sinks;
        _logger = logger;
        _options = options;
    }

    /// <summary>
    /// Abre cada sink e começa a leitura do channel.
    /// </summary>
    /// <param name="cancellationToken">Cancela a abertura dos sinks.</param>
    /// <returns>Tarefa concluída quando a leitura está em andamento.</returns>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var started = new List<IAuditSink>(_sinks.Count);
        try
        {
            foreach (IAuditSink sink in _sinks)
            {
                await sink.StartAsync(cancellationToken).ConfigureAwait(false);
                started.Add(sink);
            }
        }
        catch
        {
            await StopSinksAsync(started).ConfigureAwait(false);
            throw;
        }

        _reader = RunAsync();
    }

    /// <summary>
    /// Completa o channel, espera a fila esvaziar por no máximo 10 segundos e fecha os sinks.
    /// </summary>
    /// <param name="cancellationToken">Encurta a espera quando o host já desistiu.</param>
    /// <returns>Tarefa concluída quando os sinks foram parados.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _channel.Writer.TryComplete();
        await _shutdown.CancelAsync().ConfigureAwait(false);

        if (_reader is not null)
        {
            try
            {
                await _reader.WaitAsync(DrainTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("A drenagem da auditoria passou de {TimeoutSeconds}s e foi interrompida.", DrainTimeout.TotalSeconds);
            }
            catch (OperationCanceledException)
            {
                _logger.LogWarning("O host cancelou a drenagem da auditoria antes do channel esvaziar.");
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "A drenagem da auditoria terminou com falha.");
            }
        }

        await StopSinksAsync(_sinks).ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        var batch = new List<AuditRecord>(_options.BatchSize);
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                if (!await _channel.Reader.WaitToReadAsync(_shutdown.Token).ConfigureAwait(false))
                {
                    break;
                }

                Take(batch);
                if (batch.Count >= _options.BatchSize)
                {
                    await PublishAsync(batch).ConfigureAwait(false);
                    continue;
                }

                await WaitForMoreOrIntervalAsync(batch).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_shutdown.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "O writer de auditoria parou de ler o channel.");
        }

        Take(batch);
        if (batch.Count > 0)
        {
            await PublishAsync(batch).ConfigureAwait(false);
        }
    }

    private async Task WaitForMoreOrIntervalAsync(List<AuditRecord> batch)
    {
        using var interval = CancellationTokenSource.CreateLinkedTokenSource(_shutdown.Token);
        interval.CancelAfter(_options.FlushIntervalMs);
        try
        {
            while (batch.Count < _options.BatchSize)
            {
                if (!await _channel.Reader.WaitToReadAsync(interval.Token).ConfigureAwait(false))
                {
                    break;
                }

                Take(batch);
            }
        }
        catch (OperationCanceledException) when (!_shutdown.IsCancellationRequested)
        {
        }

        if (batch.Count > 0)
        {
            await PublishAsync(batch).ConfigureAwait(false);
        }
    }

    private void Take(List<AuditRecord> batch)
    {
        ChannelReader<AuditRecord> reader = _channel.Reader;
        while (batch.Count < _options.BatchSize && reader.TryRead(out AuditRecord? record))
        {
            batch.Add(record);
        }
    }

    private async Task PublishAsync(List<AuditRecord> batch)
    {
        AuditRecord[] snapshot = batch.ToArray();
        batch.Clear();
        foreach (IAuditSink sink in _sinks)
        {
            try
            {
                await sink.WriteBatchAsync(snapshot, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Sink {SinkName} falhou ao gravar {Count} registros de auditoria.",
                    sink.Name,
                    snapshot.Length);
            }
        }
    }

    private async Task StopSinksAsync(IReadOnlyList<IAuditSink> sinks)
    {
        for (int i = sinks.Count - 1; i >= 0; i--)
        {
            try
            {
                await sinks[i].StopAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                _logger.LogError(exception, "Sink {SinkName} falhou ao parar.", sinks[i].Name);
            }
        }
    }
}
