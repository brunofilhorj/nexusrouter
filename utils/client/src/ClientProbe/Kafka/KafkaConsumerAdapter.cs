using Confluent.Kafka;
using ClientProbe.Probe;
using ClientProbe.Telemetry;
using Microsoft.Extensions.Options;
namespace ClientProbe.Kafka;
/// <summary>
/// Consumes the raw FIX frame that the NexusRouter publishes to Kafka (null key, ASCII FIX value).
/// Each record goes through <see cref="ProbeAdapterBase.OnFrame"/>. Broker failures move the adapter
/// to <see cref="ProbeState.Faulted"/> and never reach the host.
/// </summary>
public sealed class KafkaConsumerAdapter : ProbeAdapterBase
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FaultBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);
    private IConsumer<Ignore, byte[]>? _consumer;
    private CancellationTokenSource? _loopCts;
    private Task? _loop;
    /// <summary>Creates the adapter.</summary>
    /// <param name="publisher">Telemetry entry point.</param>
    /// <param name="options">Probe options.</param>
    /// <param name="time">Clock.</param>
    public KafkaConsumerAdapter(ITelemetryPublisher publisher, IOptions<ClientProbeOptions> options, TimeProvider time)
        : base(ProbeTransport.Kafka, publisher, options, time)
    {
    }
    /// <inheritdoc />
    public override async Task ConnectAsync(ProbeProfile profile, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(profile);
        if (profile is not KafkaProfile kafka)
        {
            throw new ArgumentException("Kafka adapter requires a KafkaProfile.", nameof(profile));
        }
        await DisconnectAsync(ct).ConfigureAwait(false);
        Counters.Reset();
        SetState(ProbeState.Connecting);
        SetEndpoint(kafka.Endpoint);
        try
        {
            var config = new ConsumerConfig
            {
                BootstrapServers = kafka.BootstrapServers,
                GroupId = kafka.GroupId,
                AutoOffsetReset = string.Equals(kafka.AutoOffsetReset, "Earliest", StringComparison.OrdinalIgnoreCase)
                    ? Confluent.Kafka.AutoOffsetReset.Earliest
                    : Confluent.Kafka.AutoOffsetReset.Latest,
                EnableAutoCommit = true,
            };
            var consumer = new ConsumerBuilder<Ignore, byte[]>(config)
                .SetValueDeserializer(Deserializers.ByteArray)
                .SetErrorHandler(OnBrokerError)
                .Build();
            _consumer = consumer;
            consumer.Subscribe(kafka.Topic);
            var cts = new CancellationTokenSource();
            _loopCts = cts;
            SetState(ProbeState.Live);
            _loop = Task.Run(() => ConsumeLoop(consumer, cts.Token), CancellationToken.None);
        }
        catch (Exception ex) when (ex is KafkaException or ArgumentException or InvalidOperationException)
        {
            CloseConsumer();
            SetState(ProbeState.Faulted, ex.Message);
        }
    }
    /// <inheritdoc />
    public override async Task DisconnectAsync(CancellationToken ct)
    {
        var cts = _loopCts;
        var loop = _loop;
        _loopCts = null;
        _loop = null;
        if (cts is not null)
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }
        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(CloseTimeout, ct).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // The loop observes cancellation within one poll; closing below ends it regardless.
            }
        }
        CloseConsumer();
        cts?.Dispose();
        SetState(ProbeState.Disconnected);
        SetEndpoint("");
    }
    private void ConsumeLoop(IConsumer<Ignore, byte[]> consumer, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var result = consumer.Consume(PollTimeout);
                if (result is null || result.IsPartitionEOF)
                {
                    continue;
                }
                if (State == ProbeState.Faulted)
                {
                    SetState(ProbeState.Live);
                }
                OnFrame(result.Message?.Value ?? []);
            }
            catch (ConsumeException ex)
            {
                Fault(ex.Error);
                WaitBackoff(ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex) when (ex is KafkaException or ObjectDisposedException or InvalidOperationException)
            {
                if (!ct.IsCancellationRequested)
                {
                    SetState(ProbeState.Faulted, ex.Message);
                }
                break;
            }
        }
    }

    private void OnBrokerError(IConsumer<Ignore, byte[]> consumer, Error error)
    {
        if (error.IsFatal || error.Code == ErrorCode.Local_AllBrokersDown)
        {
            Fault(error);
        }
    }

    private void Fault(Error error)
    {
        if (_loopCts is { IsCancellationRequested: false })
        {
            SetState(ProbeState.Faulted, error.Reason);
        }
    }

    private static void WaitBackoff(CancellationToken ct)
    {
        try
        {
            Task.Delay(FaultBackoff, ct).Wait(CancellationToken.None);
        }
        catch (AggregateException)
        {
            // Cancelled while backing off; the loop condition ends the iteration.
        }
    }

    private void CloseConsumer()
    {
        var consumer = _consumer;
        _consumer = null;
        if (consumer is null)
        {
            return;
        }
        try
        {
            consumer.Close();
        }
        catch (Exception ex) when (ex is KafkaException or ObjectDisposedException or InvalidOperationException)
        {
            // Closing a consumer that never reached the broker can throw; nothing left to release.
        }
        consumer.Dispose();
    }
}
