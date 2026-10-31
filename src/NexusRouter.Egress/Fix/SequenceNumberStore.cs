using System.Globalization;

using Microsoft.Extensions.Logging;

namespace NexusRouter.Egress.Fix;

/// <summary>
/// Guarda o último MsgSeqNum enviado por um dispatcher em um arquivo local.
/// </summary>
/// <remarks>
/// <para>
/// O hot path só chama <see cref="Next"/>, que usa <see cref="Interlocked.Increment(ref long)"/>.
/// O arquivo é atualizado em background a cada 5 segundos e também em <see cref="StopAsync"/>.
/// </para>
/// <para>
/// Um crash no intervalo entre dois flushes pode repetir sequence numbers já enviados depois do último valor gravado.
/// O encerramento normal grava o contador atual.
/// </para>
/// </remarks>
public sealed class SequenceNumberStore : IAsyncDisposable
{
    private static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(5);

    private readonly string _directory;
    private readonly ILogger _logger;
    private readonly SemaphoreSlim _flushGate = new(1, 1);

    private string? _path;
    private PeriodicTimer? _timer;
    private CancellationTokenSource? _persistCts;
    private Task? _persistTask;
    private long _current;
    private long _lastFlushed = -1;
    private int _loadStarted;
    private int _loaded;
    private int _stopped;

    /// <summary>
    /// Cria o store. O arquivo é aberto em <see cref="LoadAsync"/>.
    /// </summary>
    /// <param name="directory">Diretório dos arquivos. Um arquivo por dispatcher.</param>
    /// <param name="logger">Log de falha de persistência. O número em si não é segredo, mas o frame FIX não entra aqui.</param>
    /// <exception cref="ArgumentException"><paramref name="directory"/> é vazio.</exception>
    /// <exception cref="ArgumentNullException"><paramref name="logger"/> é nulo.</exception>
    public SequenceNumberStore(string directory, ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentNullException.ThrowIfNull(logger);
        _directory = directory;
        _logger = logger;
    }

    /// <summary>
    /// Lê o último número gravado e passa a persistir em background.
    /// </summary>
    /// <param name="dispatcherId">Id do dispatcher. Vira o nome do arquivo. Só letras, dígitos, hífen e underscore.</param>
    /// <param name="cancellationToken">Cancela a leitura inicial.</param>
    /// <returns>Tarefa concluída quando o contador em memória reflete o arquivo, ou zero se o arquivo ainda não existe.</returns>
    /// <exception cref="ArgumentException"><paramref name="dispatcherId"/> contém caractere fora do conjunto permitido.</exception>
    /// <exception cref="InvalidOperationException">O arquivo existe e não contém um inteiro não negativo.</exception>
    public async ValueTask LoadAsync(string dispatcherId, CancellationToken cancellationToken)
    {
        if (Volatile.Read(ref _loaded) == 1)
        {
            return;
        }

        if (Interlocked.CompareExchange(ref _loadStarted, 1, 0) != 0)
        {
            throw new InvalidOperationException("O sequence number já está sendo carregado.");
        }

        try
        {
            string fileName = Sanitize(dispatcherId) + ".seq";
            Directory.CreateDirectory(_directory);
            _path = Path.Combine(_directory, fileName);
            if (File.Exists(_path))
            {
                string text = (await File.ReadAllTextAsync(_path, cancellationToken).ConfigureAwait(false)).Trim();
                if (!long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long stored) || stored < 0)
                {
                    throw new InvalidOperationException($"O arquivo de sequence number '{_path}' está ilegível.");
                }

                _current = stored;
                _lastFlushed = stored;
            }

            _persistCts = new CancellationTokenSource();
            _timer = new PeriodicTimer(FlushInterval);
            _persistTask = PersistLoopAsync(_persistCts.Token);
            Volatile.Write(ref _loaded, 1);
        }
        catch
        {
            _persistCts?.Cancel();
            _timer?.Dispose();
            Interlocked.Exchange(ref _loadStarted, 0);
            throw;
        }
    }

    /// <summary>
    /// Reserva o próximo MsgSeqNum.
    /// </summary>
    /// <returns>O valor a gravar na tag 34. O primeiro envio depois de um arquivo vazio é 1.</returns>
    /// <exception cref="InvalidOperationException"><see cref="LoadAsync"/> ainda não concluiu.</exception>
    /// <remarks>Zero allocation. Sem I/O. Seguro para o caminho de envio.</remarks>
    public long Next()
    {
        if (Volatile.Read(ref _loaded) == 0)
        {
            throw new InvalidOperationException("O sequence number ainda não foi carregado.");
        }

        return Interlocked.Increment(ref _current);
    }

    /// <summary>
    /// Para o timer e grava o contador atual.
    /// </summary>
    /// <param name="cancellationToken">Cancela a gravação final.</param>
    /// <returns>Tarefa concluída quando o arquivo foi atualizado ou quando não havia nada carregado.</returns>
    public async ValueTask StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        _persistCts?.Cancel();
        _timer?.Dispose();
        if (_persistTask is not null)
        {
            try
            {
                await _persistTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // O timer foi cancelado de propósito.
            }
        }

        if (Volatile.Read(ref _loaded) == 1)
        {
            await FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        _persistCts?.Dispose();
    }

    /// <summary>
    /// Para o timer e grava o contador, como <see cref="StopAsync"/>.
    /// </summary>
    /// <returns>Tarefa concluída quando o arquivo foi atualizado.</returns>
    public async ValueTask DisposeAsync() => await StopAsync(CancellationToken.None).ConfigureAwait(false);

    private async Task PersistLoopAsync(CancellationToken cancellationToken)
    {
        PeriodicTimer timer = _timer ?? throw new InvalidOperationException("O timer de sequence number não foi criado.");
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                try
                {
                    await FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Falha ao persistir o sequence number em {Path}.", _path);
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private async ValueTask FlushAsync(CancellationToken cancellationToken)
    {
        string? path = _path;
        if (path is null)
        {
            return;
        }

        await _flushGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long current = Interlocked.Read(ref _current);
            if (current == _lastFlushed)
            {
                return;
            }

            string temporary = path + ".tmp";
            string text = current.ToString(CultureInfo.InvariantCulture);
            await File.WriteAllTextAsync(temporary, text, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
            _lastFlushed = current;
        }
        finally
        {
            _flushGate.Release();
        }
    }

    private static string Sanitize(string dispatcherId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dispatcherId);
        foreach (char ch in dispatcherId)
        {
            if (ch is not ((>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_'))
            {
                throw new ArgumentException(
                    "O id do dispatcher só pode conter letras, dígitos, '-' e '_'.",
                    nameof(dispatcherId));
            }
        }

        return dispatcherId;
    }
}
