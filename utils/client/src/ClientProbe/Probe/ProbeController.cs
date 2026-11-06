using Microsoft.Extensions.Hosting;

namespace ClientProbe.Probe;

/// <summary>
/// Holds one <see cref="IProbeAdapter"/> per <see cref="ProbeTransport"/>. Connecting one transport
/// never touches the others. Stopping the host disconnects all of them.
/// </summary>
public sealed class ProbeController : IHostedService
{
    private readonly Dictionary<ProbeTransport, IProbeAdapter> _adapters;
    private readonly Dictionary<ProbeTransport, SemaphoreSlim> _gates;

    /// <summary>Creates the controller.</summary>
    /// <param name="adapters">Exactly one adapter per transport.</param>
    /// <exception cref="ArgumentException">Two adapters share a transport, or a transport has none.</exception>
    public ProbeController(IEnumerable<IProbeAdapter> adapters)
    {
        ArgumentNullException.ThrowIfNull(adapters);
        _adapters = [];
        _gates = [];
        foreach (var adapter in adapters)
        {
            if (!_adapters.TryAdd(adapter.Transport, adapter))
            {
                throw new ArgumentException($"More than one adapter for {adapter.Transport}.", nameof(adapters));
            }

            _gates[adapter.Transport] = new SemaphoreSlim(1, 1);
        }

        foreach (var transport in Enum.GetValues<ProbeTransport>())
        {
            if (!_adapters.ContainsKey(transport))
            {
                throw new ArgumentException($"No adapter for {transport}.", nameof(adapters));
            }
        }
    }

    /// <summary>Adapters in transport order: Kafka, Rabbit, FIX.</summary>
    public IReadOnlyList<IProbeAdapter> Adapters => [.. Enum.GetValues<ProbeTransport>().Select(t => _adapters[t])];

    /// <summary>
    /// Connects the transport of <paramref name="profile"/>. A live or faulted connection on the same
    /// transport is closed first. Other transports are not touched.
    /// </summary>
    /// <param name="profile">Profile to connect with.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>A task that completes when the adapter finished its connect attempt.</returns>
    /// <exception cref="ProbeConflictException">The transport is already connecting.</exception>
    public async Task ConnectAsync(ProbeProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var adapter = _adapters[profile.Transport];
        var gate = _gates[profile.Transport];
        if (!await gate.WaitAsync(0, ct).ConfigureAwait(false))
        {
            throw Conflict(profile.Transport);
        }

        try
        {
            if (adapter.State == ProbeState.Connecting)
            {
                throw Conflict(profile.Transport);
            }

            if (adapter.State != ProbeState.Disconnected)
            {
                await adapter.DisconnectAsync(ct).ConfigureAwait(false);
            }

            await adapter.ConnectAsync(profile, ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>Disconnects one transport. Idempotent.</summary>
    /// <param name="transport">Transport to close.</param>
    /// <param name="ct">Cancels the operation.</param>
    /// <returns>A task that completes when the adapter is closed.</returns>
    public async Task DisconnectAsync(ProbeTransport transport, CancellationToken ct)
    {
        var gate = _gates[transport];
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await _adapters[transport].DisconnectAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Disconnects the three adapters. One failing adapter does not stop the others.</summary>
    /// <param name="cancellationToken">Cancels the shutdown wait.</param>
    /// <returns>A task that completes when all adapters have been asked to close.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        var closes = Enum.GetValues<ProbeTransport>().Select(t => CloseQuietlyAsync(t, cancellationToken));
        await Task.WhenAll(closes).ConfigureAwait(false);
    }

    private async Task CloseQuietlyAsync(ProbeTransport transport, CancellationToken ct)
    {
        try
        {
            await DisconnectAsync(transport, ct).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Shutdown path: adapters report their own faults through LastError; keep closing the rest.
            // A cancelled token means the host gave up waiting. That must not crash the process on exit,
            // so cancellation is swallowed here too.
        }
    }

    private static ProbeConflictException Conflict(ProbeTransport transport) =>
        new($"{transport} is already connecting. Wait for it to finish or disconnect first.");
}
