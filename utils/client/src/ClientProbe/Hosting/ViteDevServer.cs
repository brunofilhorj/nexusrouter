using System.Diagnostics;
using System.Net.Sockets;

namespace ClientProbe.Hosting;

/// <summary>
/// Starts <c>npm run dev</c> in <c>ClientApp</c> and waits until Vite accepts connections
/// on an internal loopback port. The browser stays on the Kestrel origin.
/// </summary>
internal sealed class ViteDevServer : IHostedService
{
    /// <summary>Loopback address Vite binds to. This is not the operator URL.</summary>
    public const string Host = "127.0.0.1";

    /// <summary>Internal Vite port. Kestrel proxies page assets here.</summary>
    public const int Port = 5173;

    private readonly IHostEnvironment _environment;
    private readonly ILogger<ViteDevServer> _logger;
    private Process? _process;

    /// <summary>Creates the dev-server host.</summary>
    /// <param name="environment">Used to resolve <c>ClientApp</c> under the content root.</param>
    /// <param name="logger">Receives Vite output and startup failures.</param>
    public ViteDevServer(IHostEnvironment environment, ILogger<ViteDevServer> logger)
    {
        _environment = environment;
        _logger = logger;
    }

    /// <summary>Base URL of the internal Vite server.</summary>
    public string Origin => $"http://{Host}:{Port}";

    /// <summary>Starts Vite and returns when the internal port accepts a connection.</summary>
    /// <param name="cancellationToken">Cancels startup when the host is stopping.</param>
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        var clientApp = Path.Combine(_environment.ContentRootPath, "ClientApp");
        var startInfo = new ProcessStartInfo
        {
            FileName = "npm",
            WorkingDirectory = clientApp,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        startInfo.ArgumentList.Add("run");
        startInfo.ArgumentList.Add("dev");
        startInfo.ArgumentList.Add("--");
        startInfo.ArgumentList.Add("--host");
        startInfo.ArgumentList.Add(Host);
        startInfo.ArgumentList.Add("--port");
        startInfo.ArgumentList.Add(Port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add("--strictPort");

        var process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
        process.OutputDataReceived += (_, args) => LogLine(args.Data);
        process.ErrorDataReceived += (_, args) => LogLine(args.Data);

        if (!process.Start())
        {
            throw new InvalidOperationException("Failed to start the Vite dev server.");
        }

        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        _process = process;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        try
        {
            while (true)
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException($"Vite exited with code {process.ExitCode} before accepting connections.");
                }

                try
                {
                    using var tcp = new TcpClient();
                    await tcp.ConnectAsync(Host, Port, timeout.Token).ConfigureAwait(false);
                    _logger.LogInformation("Vite dev server is accepting connections at {Origin}.", Origin);
                    return;
                }
                catch (SocketException)
                {
                    await Task.Delay(200, timeout.Token).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw new InvalidOperationException($"Timed out waiting for the Vite dev server at {Origin}.");
        }
        catch
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Stops the Vite process tree.</summary>
    /// <param name="cancellationToken">Cancels the wait for process exit.</param>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var process = _process;
        if (process is null)
        {
            return;
        }

        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (InvalidOperationException)
        {
            // The process had already exited between the check and the kill.
        }
        finally
        {
            process.Dispose();
            _process = null;
        }
    }

    private void LogLine(string? line)
    {
        if (!string.IsNullOrWhiteSpace(line))
        {
            _logger.LogInformation("vite: {Line}", line);
        }
    }
}
