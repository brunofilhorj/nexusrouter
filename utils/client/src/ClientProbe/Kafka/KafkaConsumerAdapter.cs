using Confluent.Kafka;
using ClientProbe.Probe;
using ClientProbe.Telemetry;
using Microsoft.Extensions.Options;
namespace ClientProbe.Kafka;
/// <summary>
/// Consumes the raw FIX frame that the NexusRouter publishes to Kafka (null key, ASCII FIX value).
/// Each record goes through <see cref="ProbeAdapterBase.OnFrame"/>. The adapter stays in
/// <see cref="ProbeState.Connecting"/> until the broker assigns partitions, then goes
/// <see cref="ProbeState.Live"/>. Fatal broker failures move it to <see cref="ProbeState.Faulted"/> and never
/// reach the host; the transient "all brokers down" report is only noted once the consumer is live
/// (see <see cref="KafkaConsumerPolicy"/>).
/// </summary>
public sealed class KafkaConsumerAdapter : ProbeAdapterBase
{
    private static readonly TimeSpan PollTimeout = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FaultBackoff = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan CloseTimeout = TimeSpan.FromSeconds(5);
    private volatile IConsumer<Ignore, byte[]>? _consumer;
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
                if (LastError is not null)
                {
                    // A frame arrived, so an earlier "brokers are down" report is over.
                    ClearError();
                }
                OnFrame(result.Message?.Value ?? []);
            }
            catch (ConsumeException ex)
            {
                Fault(consumer, ex.Error);
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

    // Runs on the consume thread. The partitions are the signal that frames can now arrive.
    private void OnPartitionsAssigned(IConsumer<Ignore, byte[]> consumer, List<TopicPartition> partitions)
    {
        if (!IsCurrent(consumer) || partitions.Count == 0)
        {
            return;
        }

        if (State is ProbeState.Connecting or ProbeState.Faulted)
        {
            SetState(ProbeState.Live);
            ClearError();
        }
    }

    private void OnBrokerError(IConsumer<Ignore, byte[]> consumer, Error error)
    {
        if (!IsCurrent(consumer))
        {
            return;
        }

        switch (KafkaConsumerPolicy.Classify(State, error.IsFatal, error.Code))
        {
            case KafkaErrorAction.Fault:
                Fault(consumer, error);
                break;
            case KafkaErrorAction.Note:
                SetLastError(error.Reason);
                break;
        }
    }

    private void Fault(IConsumer<Ignore, byte[]> consumer, Error error)
    {
        // An error from a consumer that was already replaced must not touch the new connection.
        if (IsCurrent(consumer) && _loopCts is { IsCancellationRequested: false })
        {
            SetState(ProbeState.Faulted, error.Reason);
        }
    }

    private bool IsCurrent(IConsumer<Ignore, byte[]> consumer) => ReferenceEquals(consumer, _consumer);

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
