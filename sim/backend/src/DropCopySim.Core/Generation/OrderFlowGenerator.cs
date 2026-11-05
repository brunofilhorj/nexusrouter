using System.Buffers;
using System.Globalization;
using System.Threading.Channels;
using DropCopySim.Core.Acceptor;
using DropCopySim.Core.Telemetry;
using Microsoft.Extensions.Logging;

namespace DropCopySim.Core.Generation;

/// <summary>
/// Background loop that emits application messages at <see cref="SimulationConfig.RatePerSecond"/>
/// until a finite <see cref="SimulationConfig.TotalOrders"/> is reached or the round is stopped.
/// </summary>
/// <remarks>
/// The loop reads the published <see cref="SimulationConfig"/> with <see cref="Volatile.Read{T}(ref T)"/>
/// at the start of every cycle. Hot changes (<see cref="SimulationConfig.RatePerSecond"/> and
/// <see cref="SimulationConfig.Outcome"/>) apply on the next tick. An order already expanded by
/// <see cref="OrderLifecycleModel"/> is sent to completion with the outcome captured when it started.
/// FIX writes never await telemetry: samples use <see cref="ChannelWriter{T}.TryWrite"/>.
/// </remarks>
public sealed class OrderFlowGenerator : IAsyncDisposable
{
    private const int MaxFrameBytes = 8192;
    private const int HighRateTicksPerSecond = 1_000;
    private static readonly TimeSpan PausePollInterval = TimeSpan.FromMilliseconds(20);

    private readonly SimulationCounters _counters;
    private readonly ChannelWriter<TelemetryEvent> _telemetry;
    private readonly ILogger<OrderFlowGenerator> _logger;
    private readonly int _messageSampleMaxPerSecond;
    private readonly string _clearingFirm;
    private readonly Random _random;

    private SimulationConfig _config;
    private AcceptorSession? _session;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private IReadOnlyList<OrderContext>? _lifecycle;
    private int _lifecycleIndex;
    private long _ordersStarted;
    private long _businessSeqNum;
    private long _sampleWindowSeconds = -1;
    private int _samplesInWindow;
    private int _state = (int)SimulationState.Idle;
    private int _disposed;

    private OrderSelectionResolver? _resolver;
    private SimulationConfig? _resolverConfig;
    private IReadOnlyList<IFixMessageFactory>? _factories;
    private SimulationConfig? _factoryConfig;
    private string? _factorySender;
    private string? _factoryTarget;

    /// <summary>
    /// Creates a generator bound to an immutable config snapshot and the round counters.
    /// </summary>
    /// <param name="config">Initial snapshot. Replace it at runtime with <see cref="PublishConfig"/>.</param>
    /// <param name="counters">Round counters updated after each successful FIX send.</param>
    /// <param name="telemetry">Non-blocking sink. <see cref="ChannelWriter{T}.TryWrite"/> drops when the channel is full.</param>
    /// <param name="logger">Logger for lifecycle failures. Per-message success is not logged.</param>
    /// <param name="messageSampleMaxPerSecond">
    /// Maximum <see cref="MessageSample"/> events published per second. Zero disables samples.
    /// </param>
    /// <param name="clearingFirm">PartyRole 4 identifier copied into every order.</param>
    /// <param name="random">Outcome and random-account source. A private <see cref="Random"/> is created when null.</param>
    /// <exception cref="ArgumentNullException">A required argument is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="messageSampleMaxPerSecond"/> is negative.</exception>
    /// <exception cref="ArgumentException"><paramref name="clearingFirm"/> is blank.</exception>
    public OrderFlowGenerator(
        SimulationConfig config,
        SimulationCounters counters,
        ChannelWriter<TelemetryEvent> telemetry,
        ILogger<OrderFlowGenerator> logger,
        int messageSampleMaxPerSecond = 50,
        string clearingFirm = "CLEARING",
        Random? random = null)
    {
        ArgumentNullException.ThrowIfNull(config);
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(telemetry);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentOutOfRangeException.ThrowIfNegative(messageSampleMaxPerSecond);
        ArgumentException.ThrowIfNullOrWhiteSpace(clearingFirm);

        _config = config;
        _counters = counters;
        _telemetry = telemetry;
        _logger = logger;
        _messageSampleMaxPerSecond = messageSampleMaxPerSecond;
        _clearingFirm = clearingFirm;
        _random = random ?? new Random();
    }

    /// <summary>Current generator state.</summary>
    public SimulationState State => (SimulationState)Volatile.Read(ref _state);

    /// <summary>Counters for the active or most recently started round.</summary>
    public SimulationCounters Counters => _counters;

    /// <summary>
    /// Replaces the config snapshot the loop will observe on its next cycle.
    /// </summary>
    /// <param name="config">Immutable snapshot. The previous instance is left unchanged.</param>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    /// <exception cref="ObjectDisposedException">The generator has been disposed.</exception>
    public void PublishConfig(SimulationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Volatile.Write(ref _config, config);
    }

    /// <summary>
    /// Sets the session that receives application frames. Pass null when the client disconnects.
    /// </summary>
    /// <param name="session">Active acceptor session, or null.</param>
    /// <exception cref="ObjectDisposedException">The generator has been disposed.</exception>
    public void AttachSession(AcceptorSession? session)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        Volatile.Write(ref _session, session);
    }

    /// <summary>
    /// Moves <see cref="SimulationState.Idle"/> to <see cref="SimulationState.Running"/> and starts the loop.
    /// </summary>
    /// <exception cref="InvalidOperationException">The generator is not idle, or the config has no accounts or instruments.</exception>
    /// <exception cref="ObjectDisposedException">The generator has been disposed.</exception>
    public void Start()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var config = Volatile.Read(ref _config);
        _ = new OrderSelectionResolver(config, _random);

        if (State != SimulationState.Idle)
        {
            throw new InvalidOperationException($"Cannot start the generator from state {State}.");
        }

        var runCts = new CancellationTokenSource();
        if (Interlocked.CompareExchange(ref _state, (int)SimulationState.Running, (int)SimulationState.Idle)
            != (int)SimulationState.Idle)
        {
            runCts.Dispose();
            throw new InvalidOperationException($"Cannot start the generator from state {State}.");
        }

        _ordersStarted = 0;
        _lifecycle = null;
        _lifecycleIndex = 0;
        _counters.StartRound(DateTimeOffset.UtcNow);
        _cts = runCts;
        _loop = Task.Run(() => RunAsync(runCts));
        PublishState(SimulationState.Running, "start");
    }

    /// <summary>Suspends sending. The active order stays queued for <see cref="Resume"/>.</summary>
    /// <exception cref="InvalidOperationException">The generator is not running.</exception>
    /// <exception cref="ObjectDisposedException">The generator has been disposed.</exception>
    public void Pause()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _state, (int)SimulationState.Paused, (int)SimulationState.Running)
            != (int)SimulationState.Running)
        {
            throw new InvalidOperationException($"Cannot pause the generator from state {State}.");
        }

        PublishState(SimulationState.Paused, "pause");
    }

    /// <summary>Resumes a paused round. The next tick uses the latest published config.</summary>
    /// <exception cref="InvalidOperationException">The generator is not paused.</exception>
    /// <exception cref="ObjectDisposedException">The generator has been disposed.</exception>
    public void Resume()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        if (Interlocked.CompareExchange(ref _state, (int)SimulationState.Running, (int)SimulationState.Paused)
            != (int)SimulationState.Paused)
        {
            throw new InvalidOperationException($"Cannot resume the generator from state {State}.");
        }

        PublishState(SimulationState.Running, "resume");
    }

    /// <summary>Stops the round and waits until the loop exits. Counters are left intact.</summary>
    /// <returns>A task that completes when the background loop has exited.</returns>
    /// <exception cref="InvalidOperationException">The generator is idle.</exception>
    /// <exception cref="ObjectDisposedException">The generator has been disposed.</exception>
    public async Task StopAsync()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        var observed = Volatile.Read(ref _state);
        if (observed is not ((int)SimulationState.Running or (int)SimulationState.Paused))
        {
            throw new InvalidOperationException($"Cannot stop the generator from state {(SimulationState)observed}.");
        }

        if (Interlocked.CompareExchange(ref _state, (int)SimulationState.Idle, observed) != observed)
        {
            throw new InvalidOperationException($"Cannot stop the generator from state {State}.");
        }

        CancelRun();
        PublishState(SimulationState.Idle, "stop");
        await WaitForLoopAsync().ConfigureAwait(false);
    }

    /// <summary>Stops the loop if it is still active and releases the run token.</summary>
    /// <returns>A task that completes when the background loop has exited.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        while (true)
        {
            var observed = Volatile.Read(ref _state);
            if (observed == (int)SimulationState.Idle)
            {
                break;
            }

            if (Interlocked.CompareExchange(ref _state, (int)SimulationState.Idle, observed) == observed)
            {
                CancelRun();
                PublishState(SimulationState.Idle, "stop");
                break;
            }
        }

        await WaitForLoopAsync().ConfigureAwait(false);
    }

    private async Task RunAsync(CancellationTokenSource runCts)
    {
        var cancellationToken = runCts.Token;
        PeriodicTimer? timer = null;
        var timerRate = 0;
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var state = State;
                if (state == SimulationState.Idle)
                {
                    break;
                }

                if (state == SimulationState.Paused)
                {
                    timer?.Dispose();
                    timer = null;
                    timerRate = 0;
                    await Task.Delay(PausePollInterval, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                var config = Volatile.Read(ref _config);
                var rate = NormalizeRate(config.RatePerSecond);
                if (timer is null || rate != timerRate)
                {
                    timer?.Dispose();
                    timerRate = rate;
                    timer = new PeriodicTimer(TickPeriod(rate));
                }

                if (!await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
                {
                    break;
                }

                if (State != SimulationState.Running)
                {
                    continue;
                }

                config = Volatile.Read(ref _config);
                if (NormalizeRate(config.RatePerSecond) != timerRate)
                {
                    continue;
                }

                var batch = MessagesPerTick(timerRate);
                for (var sentInTick = 0; sentInTick < batch && State == SimulationState.Running; sentInTick++)
                {
                    config = Volatile.Read(ref _config);
                    if (NormalizeRate(config.RatePerSecond) != timerRate)
                    {
                        break;
                    }

                    if (!await SendNextAsync(config, cancellationToken).ConfigureAwait(false))
                    {
                        return;
                    }
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Stop or dispose canceled the tick wait.
        }
        catch (Exception ex)
        {
            _counters.IncrementErrors();
            _logger.LogError(ex, "Order flow generator stopped because of an unexpected error.");
            EnterIdleFromRunning("error");
        }
        finally
        {
            timer?.Dispose();
            runCts.Dispose();
        }
    }

    private async ValueTask<bool> SendNextAsync(SimulationConfig config, CancellationToken cancellationToken)
    {
        var session = ActiveSession();
        if (session is null)
        {
            return true;
        }

        if (_lifecycle is null || _lifecycleIndex >= _lifecycle.Count)
        {
            if (IsOrderLimitReached(config))
            {
                return FinishIfLimitReached();
            }

            try
            {
                BeginOrder(config);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _counters.IncrementErrors();
                _logger.LogError(ex, "Failed to build the next order lifecycle.");
                EnterIdleFromRunning("error");
                return false;
            }
        }

        var lifecycle = _lifecycle;
        if (lifecycle is null || _lifecycleIndex >= lifecycle.Count)
        {
            return true;
        }

        var context = lifecycle[_lifecycleIndex];
        long businessSeqNum;
        string msgType;
        try
        {
            (businessSeqNum, msgType) = await SendFrameAsync(session, config, context, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _counters.IncrementErrors();
            _logger.LogWarning(ex, "Failed to send an application message.");
            _lifecycleIndex++;
            return true;
        }

        _lifecycleIndex++;
        _counters.IncrementTotalSent();
        _counters.IncrementExecType(context.ExecType);
        TryPublishSample(context, msgType, businessSeqNum);

        if (_lifecycleIndex >= lifecycle.Count && IsOrderLimitReached(config))
        {
            return FinishIfLimitReached();
        }

        return true;
    }

    private bool FinishIfLimitReached()
    {
        EnterIdleFromRunning("limit");
        return State == SimulationState.Paused;
    }

    private void BeginOrder(SimulationConfig config)
    {
        var orderNumber = _ordersStarted + 1;
        var now = DateTimeOffset.UtcNow;
        var template = new OrderContext
        {
            ClOrdId = CreateClOrdId(orderNumber, now),
            OrderId = orderNumber.ToString(CultureInfo.InvariantCulture),
            ExecId = string.Create(CultureInfo.InvariantCulture, $"E{orderNumber}"),
            Side = (orderNumber & 1) == 0 ? '1' : '2',
            OrderQty = 100,
            Price = 10m,
            ClearingFirm = _clearingFirm,
            Account = "PENDING",
            Symbol = "PENDING",
            SecurityExchange = "BVMF",
        };

        var resolved = ResolverFor(config).Resolve(template);
        _lifecycle = OrderLifecycleModel.CreateLifecycle(
            resolved,
            config.Outcome,
            config.PartialFillsMax,
            _random);
        _lifecycleIndex = 0;
        _ordersStarted = orderNumber;
    }

    private async ValueTask<(long BusinessSeqNum, string MsgType)> SendFrameAsync(
        AcceptorSession session,
        SimulationConfig config,
        OrderContext context,
        CancellationToken cancellationToken)
    {
        var factory = SelectFactory(FactoriesFor(config, session));
        var seqNum = session.AllocateOutboundMsgSeqNum();
        var businessSeqNum = Interlocked.Increment(ref _businessSeqNum);
        var buffer = ArrayPool<byte>.Shared.Rent(MaxFrameBytes);
        ValueTask send;
        try
        {
            var written = factory.Build(buffer, context, seqNum, businessSeqNum);
            send = session.SendOutbound(buffer.AsSpan(0, written), cancellationToken);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        await send.ConfigureAwait(false);
        return (businessSeqNum, MsgTypeOf(factory));
    }

    private AcceptorSession? ActiveSession()
    {
        var candidate = Volatile.Read(ref _session);
        if (candidate is not null && candidate.Fsm.State == AcceptorSessionState.Active)
        {
            return candidate;
        }

        return null;
    }

    private OrderSelectionResolver ResolverFor(SimulationConfig config)
    {
        if (_resolver is not null
            && _resolverConfig is not null
            && ReferenceEquals(_resolverConfig.Accounts, config.Accounts)
            && ReferenceEquals(_resolverConfig.Instruments, config.Instruments)
            && _resolverConfig.AccountSelection == config.AccountSelection)
        {
            _resolverConfig = config;
            return _resolver;
        }

        _resolver = new OrderSelectionResolver(config, _random);
        _resolverConfig = config;
        return _resolver;
    }

    private IReadOnlyList<IFixMessageFactory> FactoriesFor(SimulationConfig config, AcceptorSession session)
    {
        var sender = session.Options.SenderCompId;
        var target = session.Options.TargetCompId;
        if (_factories is not null
            && _factoryConfig is not null
            && _factoryConfig.TestMessageIndicator == config.TestMessageIndicator
            && SameMessageTypes(_factoryConfig.EnabledMessageTypes, config.EnabledMessageTypes)
            && string.Equals(_factorySender, sender, StringComparison.Ordinal)
            && string.Equals(_factoryTarget, target, StringComparison.Ordinal))
        {
            return _factories;
        }

        _factories = ApplicationMessageFactoryCatalog.Create(sender, target, config);
        _factoryConfig = config;
        _factorySender = sender;
        _factoryTarget = target;
        return _factories;
    }

    private static IFixMessageFactory SelectFactory(IReadOnlyList<IFixMessageFactory> factories)
    {
        for (var i = 0; i < factories.Count; i++)
        {
            if (factories[i] is ExecutionReportFactory)
            {
                return factories[i];
            }
        }

        return factories[0];
    }

    private static string MsgTypeOf(IFixMessageFactory factory) => factory switch
    {
        ExecutionReportFactory => ExecutionReportFactory.MsgType,
        OrderCancelRejectFactory => OrderCancelRejectFactory.MsgType,
        BusinessMessageRejectFactory => BusinessMessageRejectFactory.MsgType,
        _ => factory.Name,
    };

    private static bool SameMessageTypes(IReadOnlyList<string> left, IReadOnlyList<string> right)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!string.Equals(left[i], right[i], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private void TryPublishSample(in OrderContext context, string msgType, long businessSeqNum)
    {
        if (_messageSampleMaxPerSecond <= 0)
        {
            return;
        }

        var now = DateTimeOffset.UtcNow;
        var window = now.ToUnixTimeSeconds();
        if (window != _sampleWindowSeconds)
        {
            _sampleWindowSeconds = window;
            _samplesInWindow = 0;
        }

        if (_samplesInWindow >= _messageSampleMaxPerSecond)
        {
            return;
        }

        _samplesInWindow++;
        _telemetry.TryWrite(new MessageSample
        {
            TimestampUtc = now,
            TsUtc = now,
            MsgType = msgType,
            ClOrdId = context.ClOrdId,
            OrderId = context.OrderId,
            ExecType = context.ExecType.ToString(),
            OrdStatus = context.OrdStatus.ToString(),
            Side = context.Side.ToString(),
            Instrument = context.Symbol,
            BusinessSeqNum = businessSeqNum,
        });
    }

    private void PublishState(SimulationState state, string reason)
    {
        _telemetry.TryWrite(new SimulationStateChanged
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            State = state,
            Reason = reason,
        });
    }

    private void EnterIdleFromRunning(string reason)
    {
        if (Interlocked.CompareExchange(ref _state, (int)SimulationState.Idle, (int)SimulationState.Running)
            == (int)SimulationState.Running)
        {
            PublishState(SimulationState.Idle, reason);
        }
    }

    private bool IsOrderLimitReached(SimulationConfig config) =>
        config.TotalOrders > 0 && _ordersStarted >= config.TotalOrders;

    private void CancelRun()
    {
        try
        {
            Volatile.Read(ref _cts)?.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The loop already disposed the run token.
        }
    }

    private async Task WaitForLoopAsync()
    {
        var loop = _loop;
        if (loop is null)
        {
            return;
        }

        try
        {
            await loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The loop exits through its own cancellation filter; this guards a faulted token.
        }

        _loop = null;
    }

    private static int NormalizeRate(int ratePerSecond) => Math.Max(1, ratePerSecond);

    private static TimeSpan TickPeriod(int ratePerSecond)
    {
        if (ratePerSecond <= HighRateTicksPerSecond)
        {
            return TimeSpan.FromTicks(TimeSpan.TicksPerSecond / ratePerSecond);
        }

        return TimeSpan.FromMilliseconds(1);
    }

    private static int MessagesPerTick(int ratePerSecond)
    {
        if (ratePerSecond <= HighRateTicksPerSecond)
        {
            return 1;
        }

        return Math.Max(1, ratePerSecond / HighRateTicksPerSecond);
    }

    private static string CreateClOrdId(long orderNumber, DateTimeOffset utcNow)
    {
        var sequence = orderNumber % 100_000_000;
        return string.Create(CultureInfo.InvariantCulture, $"{utcNow:yyyyMMdd}-{sequence:D8}");
    }
}
