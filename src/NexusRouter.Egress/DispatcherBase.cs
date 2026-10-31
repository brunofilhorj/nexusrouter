using System.Buffers;
using System.Threading.Channels;

using Microsoft.Extensions.Logging;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Models;

namespace NexusRouter.Egress;

/// <summary>
/// Loop de consumo, channel limitado e ciclo de vida comuns aos dispatchers.
/// </summary>
/// <typeparam name="TOptions">Options da implementação. Cada dispatcher guarda as suas.</typeparam>
/// <remarks>
/// <para>
/// <see cref="TryEnqueue"/> só escreve no channel. A publicação acontece na task de consumo.
/// Uma falha de envio é registrada e o loop segue. O buffer do payload é sempre liberado.
/// </para>
/// <para>
/// A continuação do channel não roda na thread que chamou <see cref="TryEnqueue"/>.
/// </para>
/// </remarks>
public abstract class DispatcherBase<TOptions> : IMessageDispatcher
    where TOptions : class
{
    private static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);

    private readonly Channel<DispatchPayload> _channel;
    private readonly ILogger _logger;
    private int _healthy;
    private CancellationTokenSource? _consumeCts;
    private Task? _consumeTask;
    private int _started;
    private int _stopRequested;

    /// <summary>
    /// Cria o channel e deixa o dispatcher parado até <see cref="StartAsync"/>.
    /// </summary>
    /// <param name="dispatcherId">Nome único, usado na rota e na auditoria.</param>
    /// <param name="options">Options já validadas pela implementação.</param>
    /// <param name="channelCapacity">Quantidade máxima de payloads no channel.</param>
    /// <param name="logger">Log do loop de consumo.</param>
    /// <exception cref="ArgumentException"><paramref name="dispatcherId"/> é vazio.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="options"/> ou <paramref name="logger"/> é nulo.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channelCapacity"/> não é positivo.</exception>
    protected DispatcherBase(string dispatcherId, TOptions options, int channelCapacity, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dispatcherId);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(channelCapacity);

        DispatcherId = dispatcherId;
        Options = options;
        _logger = logger;
        _channel = Channel.CreateBounded<DispatchPayload>(new BoundedChannelOptions(channelCapacity)
        {
            // Wait + TryWrite devolve false com o channel cheio, e o chamador continua dono do buffer.
            // DropWrite também descarta, mas TryWrite devolve true e o fan-out perderia o buffer.
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    /// <summary>
    /// Nome único do dispatcher.
    /// </summary>
    public string DispatcherId { get; }

    /// <summary>
    /// Options recebidas no construtor.
    /// </summary>
    protected TOptions Options { get; }

    /// <summary>
    /// Log da implementação. A publicação não escreve o frame FIX aqui.
    /// </summary>
    protected ILogger Logger => _logger;

    /// <summary>
    /// <see langword="true"/> depois de <see cref="StartAsync"/> conectar e antes de um erro fatal ou de <see cref="StopAsync"/>.
    /// </summary>
    /// <remarks>Leitura sem lock, via <see cref="Volatile.Read(ref int)"/>.</remarks>
    public bool IsHealthy => Volatile.Read(ref _healthy) == 1;

    /// <summary>
    /// Disparado na task de consumo quando a publicação falha.
    /// </summary>
    public event Action<string, Exception>? OnDispatchError;

    /// <summary>
    /// Conecta o destino e passa a aceitar <see cref="TryEnqueue"/>.
    /// </summary>
    /// <param name="cancellationToken">Cancela a conexão. Não encerra o loop depois que ele já começou.</param>
    /// <returns>Tarefa concluída quando o loop de consumo está ativo.</returns>
    /// <exception cref="InvalidOperationException">O dispatcher já foi iniciado.</exception>
    public virtual async ValueTask StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.CompareExchange(ref _started, 1, 0) != 0)
        {
            throw new InvalidOperationException($"O dispatcher {DispatcherId} já foi iniciado.");
        }

        await ConnectAsync(cancellationToken).ConfigureAwait(false);
        Volatile.Write(ref _healthy, 1);
        _consumeCts = new CancellationTokenSource();
        _consumeTask = Task.Run(() => ConsumeAsync(_consumeCts.Token));
    }

    /// <summary>
    /// Completa o channel, espera o loop drenar e marca o dispatcher como indisponível.
    /// </summary>
    /// <param name="cancellationToken">Abandona a espera. O channel já foi completado.</param>
    /// <returns>Tarefa concluída quando o loop termina ou quando o tempo de espera acaba.</returns>
    public virtual async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopRequested, 1) != 0)
        {
            return;
        }

        _channel.Writer.TryComplete();

        Task? consumeTask = _consumeTask;
        if (consumeTask is not null)
        {
            try
            {
                await consumeTask.WaitAsync(StopTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                _consumeCts?.Cancel();
                _logger.LogWarning(
                    "Dispatcher {DispatcherId} não encerrou o consumo em {TimeoutSeconds}s.",
                    DispatcherId,
                    StopTimeout.TotalSeconds);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                _consumeCts?.Cancel();
                _logger.LogWarning("A espera pelo dispatcher {DispatcherId} foi cancelada.", DispatcherId);
            }
        }
        else
        {
            _consumeCts?.Cancel();
            ReleasePending();
        }

        Volatile.Write(ref _healthy, 0);
    }

    /// <summary>
    /// Enfileira o payload sem bloquear.
    /// </summary>
    /// <param name="payload">Mensagem roteada. O dispatcher fica dono do buffer quando o retorno é <see langword="true"/>.</param>
    /// <returns><see langword="false"/> quando o channel está cheio ou já foi completado.</returns>
    /// <remarks>Não bloqueia. Não é o lugar de I/O. Channel cheio não lança exceção.</remarks>
    public bool TryEnqueue(DispatchPayload payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        return _channel.Writer.TryWrite(payload);
    }

    /// <summary>
    /// Abre a conexão ou o cliente do destino antes do loop de consumo.
    /// </summary>
    /// <param name="cancellationToken">Cancela a abertura.</param>
    /// <returns>Tarefa concluída quando o destino aceita envio.</returns>
    protected abstract ValueTask ConnectAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Publica um payload. Quem implementa não descarta <see cref="DispatchPayload.RawBuffer"/>.
    /// </summary>
    /// <param name="payload">Payload ainda dono do buffer.</param>
    /// <param name="cancellationToken">Cancela este envio quando o loop está parando por timeout.</param>
    /// <returns>Tarefa concluída quando o destino recebeu ou enfileirou a mensagem.</returns>
    protected abstract ValueTask SendAsync(DispatchPayload payload, CancellationToken cancellationToken);

    /// <summary>
    /// Registra a falha e avisa os assinantes. Uma falha no assinante não volta ao loop.
    /// </summary>
    /// <param name="exception">Falha da publicação ou do callback do broker.</param>
    protected void ReportDispatchError(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        _logger.LogError(exception, "Dispatcher {DispatcherId} falhou ao publicar.", DispatcherId);

        Action<string, Exception>? handler = OnDispatchError;
        if (handler is null)
        {
            return;
        }

        try
        {
            handler.Invoke(DispatcherId, exception);
        }
        catch (Exception callbackError)
        {
            _logger.LogError(
                callbackError,
                "O assinante de erro do dispatcher {DispatcherId} falhou.",
                DispatcherId);
        }
    }

    private async Task ConsumeAsync(CancellationToken cancellationToken)
    {
        try
        {
            await foreach (DispatchPayload payload in _channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await SendAsync(payload, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    ReportDispatchError(ex);
                }
                finally
                {
                    ReleasePayload(payload);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            ReleasePending();
        }
        catch (Exception ex)
        {
            Volatile.Write(ref _healthy, 0);
            _channel.Writer.TryComplete(ex);
            ReleasePending();
            ReportDispatchError(ex);
        }
    }

    private void ReleasePending()
    {
        while (_channel.Reader.TryRead(out DispatchPayload? payload))
        {
            if (payload is not null)
            {
                ReleasePayload(payload);
            }
        }
    }

    private static void ReleasePayload(DispatchPayload payload)
    {
        IMemoryOwner<byte>? buffer = payload.RawBuffer;
        payload.RawBuffer = null;
        buffer?.Dispose();
        payload.Reset();
    }
}
