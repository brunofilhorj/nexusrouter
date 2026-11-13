using System.Net;
using System.Net.Sockets;
using DropCopySim.Core.Generation;
using DropCopySim.Core.Telemetry;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace DropCopySim.Core.Acceptor;

/// <summary>
/// Listens for inbound FIX connections and runs one <see cref="AcceptorSession"/> at a time.
/// </summary>
public sealed class FixAcceptorService : IHostedService
{
    private readonly AcceptorOptions _options;
    private readonly ILogger<FixAcceptorService> _logger;
    private readonly ILogger<AcceptorSession> _sessionLogger;
    private readonly OrderFlowGenerator _generator;
    private readonly TelemetryHub _telemetry;
    private TcpListener? _listener;
    private CancellationTokenSource? _stoppingCts;
    private Task? _acceptLoop;
    private AcceptorSession? _activeSession;

    /// <summary>
    /// Creates a FIX acceptor service.
    /// </summary>
    /// <param name="options">Validated listener and session options.</param>
    /// <param name="logger">Logger for listener lifecycle events.</param>
    /// <param name="sessionLogger">Logger supplied to each accepted session.</param>
    /// <param name="generator">Order flow generator wired to the active session.</param>
    /// <param name="telemetry">Telemetry hub the session's state transitions are broadcast through.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public FixAcceptorService(
        IOptions<AcceptorOptions> options,
        ILogger<FixAcceptorService> logger,
        ILogger<AcceptorSession> sessionLogger,
        OrderFlowGenerator generator,
        TelemetryHub telemetry)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(sessionLogger);
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(telemetry);

        _options = options.Value;
        _logger = logger;
        _sessionLogger = sessionLogger;
        _generator = generator;
        _telemetry = telemetry;
    }

    /// <summary>Gets the currently connected session, if one has been accepted.</summary>
    public AcceptorSession? ActiveSession => Volatile.Read(ref _activeSession);

    /// <summary>Starts listening for inbound FIX connections.</summary>
    /// <param name="cancellationToken">Cancels listener startup.</param>
    /// <returns>A task that completes once the listener and accept loop are started.</returns>
    /// <exception cref="NotSupportedException">TLS is enabled but not supported by this service version.</exception>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_options.Tls.Enabled)
        {
            throw new NotSupportedException(
                "TLS is enabled, but TLS server streams are not supported by FixAcceptorService.");
        }

        IPAddress listenAddress = await ResolveListenAddressAsync(
            _options.ListenAddress,
            cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var listener = new TcpListener(listenAddress, _options.ListenPort);
        listener.Start();

        _listener = listener;
        _stoppingCts = new CancellationTokenSource();
        _acceptLoop = AcceptLoopAsync(listener, _stoppingCts.Token);

        _logger.LogInformation(
            "FIX acceptor listening on {ListenAddress}:{ListenPort}",
            listenAddress,
            _options.ListenPort);
    }

    /// <summary>Stops accepting connections and closes the active session.</summary>
    /// <param name="cancellationToken">Cancels waiting for service shutdown to finish.</param>
    /// <returns>A task that completes when the accept loop has stopped.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        CancellationTokenSource? stoppingCts = Interlocked.Exchange(ref _stoppingCts, null);
        if (stoppingCts is null)
        {
            return;
        }

        stoppingCts.Cancel();
        Interlocked.Exchange(ref _listener, null)?.Stop();

        AcceptorSession? activeSession = ActiveSession;
        if (activeSession is not null)
        {
            await activeSession.DisposeAsync().ConfigureAwait(false);
        }

        try
        {
            Task? acceptLoop = Interlocked.Exchange(ref _acceptLoop, null);
            if (acceptLoop is not null)
            {
                // The accept loop was already signalled to stop (stoppingCts cancelled,
                // listener closed, active session disposed above). If the host's own
                // shutdown deadline elapses before the loop winds down, do not let the
                // resulting cancellation escape StopAsync — that would surface as an
                // unhandled exception and fault the process on shutdown. Log and move on.
                await acceptLoop.WaitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogDebug(
                "FIX acceptor shutdown deadline elapsed before the accept loop stopped; abandoning the wait.");
        }
        finally
        {
            stoppingCts.Dispose();
        }

        _logger.LogInformation("FIX acceptor stopped.");
    }

    private async Task AcceptLoopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            Socket socket;
            try
            {
                Socket? accepted = await AcceptOrStopAsync(listener, cancellationToken).ConfigureAwait(false);
                if (accepted is null)
                {
                    break;
                }

                socket = accepted;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                // StopAsync closes the listener, which disposes the underlying socket.
                // A pending AcceptSocketAsync can observe that disposal as an
                // ObjectDisposedException instead of cancellation; treat it as a clean stop.
                break;
            }

            var session = new AcceptorSession(socket, _options, _sessionLogger);
            Volatile.Write(ref _activeSession, session);

            void OnStateChanged(SessionStateChanged e)
            {
                // Forward the transition to the telemetry hub so the SimPanel's session panel updates
                // live over the WebSocket feed (FR-API-02); without this the UI only ever sees the
                // one-shot GET /v1/state snapshot taken on (re)connect.
                _telemetry.TryPublish(e);

                if (string.Equals(e.State, nameof(AcceptorSessionState.Active), StringComparison.Ordinal))
                {
                    try
                    {
                        _generator.AttachSession(session);
                    }
                    catch (ObjectDisposedException)
                    {
                        _logger.LogDebug("Generator disposed; skipped attaching the active session.");
                    }
                }
                else if (string.Equals(e.State, nameof(AcceptorSessionState.Disconnected), StringComparison.Ordinal))
                {
                    try
                    {
                        _generator.AttachSession(null);
                    }
                    catch (ObjectDisposedException)
                    {
                        _logger.LogDebug("Generator disposed; skipped detaching the disconnected session.");
                    }
                }
            }

            session.StateChanged += OnStateChanged;
            try
            {
                await session.RunAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Accepted FIX session failed; the listener will continue.");
            }
            finally
            {
                await session.DisposeAsync().ConfigureAwait(false);
                session.StateChanged -= OnStateChanged;
                try
                {
                    _generator.AttachSession(null);
                }
                catch (ObjectDisposedException)
                {
                    _logger.LogDebug("Generator disposed; skipped detaching on session teardown.");
                }

                Interlocked.CompareExchange(ref _activeSession, null, session);
            }
        }
    }

    /// <summary>
    /// Accepts the next socket, or returns null when <paramref name="cancellationToken"/> is canceled.
    /// </summary>
    /// <remarks>
    /// <see cref="TcpListener.Stop"/> does not always complete a pending
    /// <see cref="TcpListener.AcceptSocketAsync(CancellationToken)"/>. Waiting on the token as well
    /// lets the accept loop leave on shutdown instead of blocking until the host deadline.
    /// </remarks>
    private static async Task<Socket?> AcceptOrStopAsync(TcpListener listener, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return null;
        }

        Task<Socket> acceptTask = listener.AcceptSocketAsync(cancellationToken).AsTask();
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await using CancellationTokenRegistration registration = cancellationToken.UnsafeRegister(
            static state => ((TaskCompletionSource)state!).TrySetResult(),
            stopped);

        Task finished = await Task.WhenAny(acceptTask, stopped.Task).ConfigureAwait(false);
        if (!cancellationToken.IsCancellationRequested && finished == acceptTask)
        {
            return await acceptTask.ConfigureAwait(false);
        }

        AbandonAccept(acceptTask);
        return null;
    }

    private static void AbandonAccept(Task<Socket> acceptTask)
    {
        _ = acceptTask.ContinueWith(
            static task =>
            {
                if (task.IsCompletedSuccessfully)
                {
                    task.Result.Dispose();
                    return;
                }

                _ = task.Exception;
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private static async Task<IPAddress> ResolveListenAddressAsync(
        string address,
        CancellationToken cancellationToken)
    {
        if (IPAddress.TryParse(address, out IPAddress? ipAddress))
        {
            return ipAddress;
        }

        IPAddress[] addresses = await Dns.GetHostAddressesAsync(address, cancellationToken).ConfigureAwait(false);
        if (addresses.Length == 0)
        {
            throw new SocketException((int)SocketError.HostNotFound);
        }

        return addresses[0];
    }
}
