using System.Net;
using System.Net.Sockets;
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
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    public FixAcceptorService(
        IOptions<AcceptorOptions> options,
        ILogger<FixAcceptorService> logger,
        ILogger<AcceptorSession> sessionLogger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(sessionLogger);

        _options = options.Value;
        _logger = logger;
        _sessionLogger = sessionLogger;
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
                socket = await listener.AcceptSocketAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            var session = new AcceptorSession(socket, _options, _sessionLogger);
            Volatile.Write(ref _activeSession, session);
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
                Interlocked.CompareExchange(ref _activeSession, null, session);
            }
        }
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
