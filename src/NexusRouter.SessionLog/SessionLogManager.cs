using System.Buffers;
using System.Text.Json;
using System.Threading.Channels;

using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

using NexusRouter.Domain.Abstractions;
using NexusRouter.Domain.Enums;
using NexusRouter.Domain.Models;

namespace NexusRouter.SessionLog;

/// <summary>
/// Owns the session-log directory: recovery, flush, pre-allocation, and retention.
/// </summary>
public sealed class SessionLogManager : IHostedService, ISessionLog, ISessionLogReader, IDisposable, IAsyncDisposable
{
    private static readonly JsonSerializerOptions CatalogJson = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    private readonly SessionLogOptions _options;
    private readonly ILogger<SessionLogManager> _logger;
    private readonly TimeProvider _timeProvider;
    private readonly SessionLogMetrics _metrics = new();
    private readonly Dictionary<string, SessionLogStream> _streams = new(StringComparer.Ordinal);
    private readonly Dictionary<ushort, SessionLogStream> _byId = [];
    private readonly Dictionary<string, ClientBinding> _clients = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConsumerStateStore> _brokers = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConsumerStateStore> _streamStates = new(StringComparer.Ordinal);
    private readonly List<ConsumerStateStore> _states = [];
    private readonly object _gate = new();
    private readonly Channel<byte> _flushSignal = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
    {
        FullMode = BoundedChannelFullMode.DropWrite,
        SingleReader = true,
        SingleWriter = false,
    });

    private Catalog _catalog = new();
    private CancellationTokenSource? _cts;
    private Task? _flushTask;
    private Task? _maintainTask;
    private int _tradingDay;
    private TimeZoneInfo _rolloverTimeZone = TimeZoneInfo.Utc;
    private bool _applyUnclean;
    private int _started;
    private int _stopped;

    /// <summary>
    /// Creates the manager. Files open in <see cref="StartAsync"/>.
    /// </summary>
    /// <param name="options">Session-log section.</param>
    /// <param name="logger">Recovery, flush, and retention log.</param>
    public SessionLogManager(
        SessionLogOptions options,
        ILogger<SessionLogManager> logger,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public TradingDayStreamSnapshot[] GetTradingDayStreams()
    {
        lock (_gate)
        {
            var snapshots = new List<TradingDayStreamSnapshot>();
            foreach (SessionLogStream stream in _streams.Values)
            {
                snapshots.AddRange(stream.GetTradingDaySnapshots(Volatile.Read(ref _tradingDay)));
            }

            snapshots.Sort(static (left, right) =>
            {
                int byStream = left.Stream.CompareTo(right.Stream);
                return byStream != 0 ? byStream : left.Epoch.CompareTo(right.Epoch);
            });
            return [.. snapshots];
        }
    }

    /// <inheritdoc />
    public event Func<CancellationToken, ValueTask>? DayRolloverStarting;

    /// <inheritdoc />
    public event Action? DayRolloverCompleted;

    /// <summary>
    /// When true, the background task does not pre-allocate the next segment.
    /// </summary>
    internal bool PauseMaintenance { get; set; }

    /// <inheritdoc />
    public ISessionLogReader Reader => this;

    /// <summary>
    /// Removes the clean marker, recovers streams and clients, and starts the background tasks.
    /// </summary>
    /// <param name="cancellationToken">Cancels startup.</param>
    /// <returns>A completed task once the directory is open.</returns>
    public Task StartAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
        {
            return Task.CompletedTask;
        }

        Microsoft.Extensions.Options.ValidateOptionsResult validation = _options.Validate(null, _options);
        if (validation.Failed)
        {
            throw new InvalidOperationException("SessionLog options are invalid: " + string.Join("; ", validation.Failures));
        }

        _rolloverTimeZone = TimeZoneInfo.FindSystemTimeZoneById(_options.DayRolloverTimeZone);
        Directory.CreateDirectory(_options.Directory);
        _tradingDay = CurrentTradingDay();
        string clean = CleanPath();
        _applyUnclean = !File.Exists(clean);
        if (File.Exists(clean))
        {
            File.Delete(clean);
        }

        LoadCatalog();
        foreach (string sessionId in _catalog.Sessions.Keys.ToArray())
        {
            OpenStream(sessionId);
        }

        RecoverClients();
        RecoverBrokers();
        _applyUnclean = false;
        ApplyRetention();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _flushTask = FlushLoopAsync(_cts.Token);
        if (!PauseMaintenance)
        {
            _maintainTask = MaintainLoopAsync(_cts.Token);
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Flushes state, writes the clean marker, and releases the maps.
    /// </summary>
    /// <param name="cancellationToken">Cancels the wait for the background tasks.</param>
    /// <returns>A task that finishes after the clean marker is stored.</returns>
    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        _cts?.Cancel();
        await WaitAsync(_flushTask, cancellationToken).ConfigureAwait(false);
        await WaitAsync(_maintainTask, cancellationToken).ConfigureAwait(false);
        FlushAll();
        Directory.CreateDirectory(_options.Directory);
        using (File.Create(CleanPath()))
        {
        }

        DisposeMaps();
        _metrics.Dispose();
    }

    /// <inheritdoc />
    public ISessionLogStream OpenStream(string sessionId)
    {
        SessionLogPaths.EnsureKey(sessionId, nameof(sessionId));
        lock (_gate)
        {
            if (_streams.TryGetValue(sessionId, out SessionLogStream? existing))
            {
                return existing;
            }

            ushort id = AssignId(sessionId);
            long capacity = _options.SegmentSizeMB * 1024L * 1024L;
            var stream = new SessionLogStream(
                _options.Directory,
                sessionId,
                id,
                capacity,
                _options.IndexSlotsPerSegment,
                Volatile.Read(ref _tradingDay),
                _logger,
                _metrics);
            _streams[sessionId] = stream;
            _byId[id] = stream;
            SaveCatalog();
            return stream;
        }
    }

    /// <inheritdoc />
    public IConsumerState OpenStreamState(string sessionId)
    {
        SessionLogPaths.EnsureKey(sessionId, nameof(sessionId));
        lock (_gate)
        {
            if (_streamStates.TryGetValue(sessionId, out ConsumerStateStore? existing))
            {
                return existing;
            }

            string path = SessionLogPaths.Under(_options.Directory, "streams", sessionId, "state.json");
            var store = new ConsumerStateStore(path, readNextOut: null, Changed);
            store.Load();
            _streamStates[sessionId] = store;
            _states.Add(store);
            return store;
        }
    }

    /// <inheritdoc />
    public ClientBinding OpenClient(string dispatcherId, string clientKey)
    {
        SessionLogPaths.EnsureKey(dispatcherId, nameof(dispatcherId));
        SessionLogPaths.EnsureKey(clientKey, nameof(clientKey));
        string cacheKey = dispatcherId + "/" + clientKey;
        lock (_gate)
        {
            if (_clients.TryGetValue(cacheKey, out ClientBinding existing))
            {
                return existing;
            }

            ClientBinding created = CreateClient(dispatcherId, clientKey, _applyUnclean);
            _clients[cacheKey] = created;
            return created;
        }
    }

    /// <inheritdoc />
    public ClientBinding ResetClient(string dispatcherId, string clientKey)
    {
        lock (_gate)
        {
            ClientBinding binding = OpenClient(dispatcherId, clientKey);
            if (binding.Index is not ClientIndex index)
            {
                throw new InvalidOperationException("The client index cannot start a new epoch.");
            }

            int epoch = index.ResetEpoch();
            binding.State.Epoch = epoch;
            return binding;
        }
    }

    /// <inheritdoc />
    public int CopyClients(string dispatcherId, Span<ClientBinding> destination)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dispatcherId);
        string prefix = dispatcherId + "/";
        lock (_gate)
        {
            int total = 0;
            foreach ((string key, ClientBinding binding) in _clients)
            {
                if (!key.StartsWith(prefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (total < destination.Length)
                {
                    destination[total] = binding;
                }

                total++;
            }

            return total;
        }
    }

    /// <inheritdoc />
    public IConsumerState OpenBroker(string dispatcherId)
    {
        SessionLogPaths.EnsureKey(dispatcherId, nameof(dispatcherId));
        lock (_gate)
        {
            if (_brokers.TryGetValue(dispatcherId, out ConsumerStateStore? existing))
            {
                return existing;
            }

            string path = SessionLogPaths.Under(_options.Directory, "brokers", dispatcherId, "cursor.json");
            var store = new ConsumerStateStore(path, readNextOut: null, Changed);
            store.Load();
            _brokers[dispatcherId] = store;
            _states.Add(store);
            return store;
        }
    }

    /// <inheritdoc />
    public bool TryRead(ushort stream, int epoch, long inSeq, IBufferWriter<byte> destination)
    {
        SessionLogStream? found = Find(stream);
        return found is not null && found.TryRead(epoch, inSeq, destination);
    }

    /// <inheritdoc />
    public bool TryRead(
        ushort stream,
        int epoch,
        long inSeq,
        IBufferWriter<byte> destination,
        out long ingressTimestampTicks)
    {
        SessionLogStream? found = Find(stream);
        if (found is not null)
        {
            return found.TryRead(epoch, inSeq, destination, out ingressTimestampTicks);
        }

        ingressTimestampTicks = 0;
        return false;
    }

    /// <inheritdoc />
    public bool TryReadAt(ushort stream, uint segment, ulong offset, IBufferWriter<byte> destination)
    {
        SessionLogStream? found = Find(stream);
        return found is not null && found.TryReadAt(segment, offset, destination);
    }

    /// <inheritdoc />
    public long GetCommitted(ushort stream, int epoch) => Find(stream)?.GetCommitted(epoch) ?? 0;

    /// <summary>
    /// Writes every consumer state file. Used by tests and by the flusher.
    /// </summary>
    public void FlushNow() => FlushAll();

    /// <summary>
    /// Drops the maps without writing the clean marker.
    /// </summary>
    internal void SimulateCrash()
    {
        _cts?.Cancel();
        WaitAsync(_flushTask, CancellationToken.None).GetAwaiter().GetResult();
        WaitAsync(_maintainTask, CancellationToken.None).GetAwaiter().GetResult();
        DisposeMaps();
        Interlocked.Exchange(ref _stopped, 1);
    }

    /// <summary>
    /// Stops the background tasks and releases the maps when <see cref="StopAsync"/> did not run.
    /// </summary>
    public void Dispose()
    {
        if (Volatile.Read(ref _stopped) == 0 && Volatile.Read(ref _started) == 1)
        {
            SimulateCrash();
            return;
        }

        ReleaseUnstartedMaps();
    }

    /// <summary>
    /// Flushes and writes the clean marker when the manager is still running.
    /// </summary>
    /// <returns>A task that finishes after shutdown.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Volatile.Read(ref _stopped) == 0 && Volatile.Read(ref _started) == 1)
        {
            await StopAsync(CancellationToken.None).ConfigureAwait(false);
            return;
        }

        ReleaseUnstartedMaps();
    }

    /// <summary>
    /// Libera streams e índices abertos quando o manager nunca foi iniciado. Sem isso, o mapeamento fica
    /// aberto até o fim do processo e, no Windows, bloqueia outra abertura dos mesmos arquivos.
    /// </summary>
    private void ReleaseUnstartedMaps()
    {
        if (Volatile.Read(ref _started) == 1 || Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        DisposeMaps();
    }

    private ClientBinding CreateClient(string dispatcherId, string clientKey, bool unclean)
    {
        string directory = SessionLogPaths.Under(_options.Directory, "clients", dispatcherId, clientKey);
        Directory.CreateDirectory(directory);
        string statePath = Path.Combine(directory, "state.json");
        ClientIndex? index = null;
        var store = new ConsumerStateStore(statePath, () => index!.NextOut, Changed);
        ConsumerStateStore.StateDocument? document = store.Load();
        long nextOut = document is null || document.NextOut < 1 ? 1 : document.NextOut;
        bool hasIndex = ClientIndex.TryGetLatestIndex(directory, out DateOnly indexedDay, out int indexedEpoch);
        int epoch = document is null ? indexedEpoch : Math.Max((int)document.Epoch, indexedEpoch);
        bool newTradingDay = hasIndex
            ? indexedDay != TradingDate(_tradingDay)
            : document is not null && document.TradingDay != 0 && document.TradingDay != _tradingDay;
        bool indexHasNewEpoch = hasIndex && document is not null && indexedEpoch > document.Epoch;
        if (newTradingDay || indexHasNewEpoch)
        {
            if (newTradingDay)
            {
                epoch++;
            }

            nextOut = 1;
            store.Epoch = epoch;
            store.NextInExpected = 1;
        }

        store.TradingDay = _tradingDay;

        // A varredura abre cada .cidx; precisa vir antes do ClientIndex mapear o arquivo do dia.
        // No Windows, o mapeamento aberto impede outra abertura do mesmo arquivo.
        long uncleanFloor = 0;
        if (unclean && document is not null && !newTradingDay && !indexHasNewEpoch)
        {
            long last = ClientIndex.ScanLastPublished(directory, _options.IndexSlotsPerSegment);
            uncleanFloor = Math.Max(nextOut, last + 1) + _options.UncleanRestartSeqMargin;
        }

        index = new ClientIndex(
            dispatcherId + "/" + clientKey,
            directory,
            _options.IndexSlotsPerSegment,
            epoch,
            nextOut,
            () => Volatile.Read(ref _tradingDay),
            Locate);
        if (uncleanFloor > 0)
        {
            index.SetNextOut(uncleanFloor);
        }

        _states.Add(store);
        return new ClientBinding(index, store);
    }

    private LogPosition? Locate(LogRef logRef)
    {
        SessionLogStream? stream = Find(logRef.Stream);
        return stream?.Locate(logRef);
    }

    private SessionLogStream? Find(ushort streamId)
    {
        lock (_gate)
        {
            return _byId.TryGetValue(streamId, out SessionLogStream? stream) ? stream : null;
        }
    }

    private void RecoverClients()
    {
        string root = Path.Combine(_options.Directory, "clients");
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (string dispatcherDir in Directory.GetDirectories(root))
        {
            string dispatcherId = Path.GetFileName(dispatcherDir);
            foreach (string clientDir in Directory.GetDirectories(dispatcherDir))
            {
                OpenClient(dispatcherId, Path.GetFileName(clientDir));
            }
        }
    }

    private void RecoverBrokers()
    {
        string root = Path.Combine(_options.Directory, "brokers");
        if (!Directory.Exists(root))
        {
            return;
        }

        foreach (string dispatcherDir in Directory.GetDirectories(root))
        {
            OpenBroker(Path.GetFileName(dispatcherDir));
        }
    }

    private void ApplyRetention()
    {
        DateOnly cutoff = TradingDate(CurrentTradingDay()).AddDays(-_options.RetentionTradingDays);
        string streams = Path.Combine(_options.Directory, "streams");
        if (Directory.Exists(streams))
        {
            foreach (string sessionDir in Directory.GetDirectories(streams))
            {
                foreach (string dayDir in Directory.GetDirectories(sessionDir))
                {
                    if (SessionLogPaths.TryParseTradingDay(Path.GetFileName(dayDir), out DateOnly day) && day < cutoff)
                    {
                        Directory.Delete(dayDir, recursive: true);
                    }
                }
            }
        }

        string clients = Path.Combine(_options.Directory, "clients");
        if (!Directory.Exists(clients))
        {
            return;
        }

        foreach (string dispatcherDir in Directory.GetDirectories(clients))
        {
            foreach (string clientDir in Directory.GetDirectories(dispatcherDir))
            {
                foreach (string file in Directory.GetFiles(clientDir, "*.cidx"))
                {
                    if (SessionLogPaths.TryParseTradingDay(Path.GetFileName(file), out DateOnly day) && day < cutoff)
                    {
                        File.Delete(file);
                    }
                }
            }
        }
    }

    private async Task FlushLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (_options.FlushMode == SessionLogFlushMode.PerMessage)
            {
                while (await _flushSignal.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    while (_flushSignal.Reader.TryRead(out _))
                    {
                    }

                    FlushAll();
                }

                return;
            }

            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(_options.FlushIntervalMs));
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                FlushAll();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The session-log flusher stopped.");
        }
    }

    private async Task MaintainLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await CheckDayRolloverAsync(cancellationToken).ConfigureAwait(false);
                SessionLogStream[] streams = SnapshotStreams();
                foreach (SessionLogStream stream in streams)
                {
                    stream.Maintain(Volatile.Read(ref _tradingDay));
                }

                await Task.Delay(TimeSpan.FromMilliseconds(Math.Max(_options.CatchUpIdleMs, 50)), cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "The session-log maintenance task stopped.");
        }
    }

    private async ValueTask CheckDayRolloverAsync(CancellationToken cancellationToken)
    {
        int day = CurrentTradingDay();
        if (day == Volatile.Read(ref _tradingDay))
        {
            return;
        }

        try
        {
            await NotifyDayRolloverStartingAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate)
            {
                Volatile.Write(ref _tradingDay, day);
                foreach (SessionLogStream stream in _streams.Values)
                {
                    stream.Maintain(day);
                }

                foreach (ClientBinding binding in _clients.Values)
                {
                    if (binding.Index is not ClientIndex index)
                    {
                        throw new InvalidOperationException("The client index cannot start a new trading day.");
                    }

                    if (binding.State is not ConsumerStateStore store)
                    {
                        throw new InvalidOperationException("The client state cannot track its trading day.");
                    }

                    store.Epoch = index.ResetEpoch();
                    store.NextInExpected = 1;
                    store.TradingDay = day;
                }
            }

            ApplyRetention();
            _logger.LogInformation(
                "Session-log storage advanced to trading day {TradingDay} in time zone {TimeZone}.",
                day,
                _options.DayRolloverTimeZone);
        }
        finally
        {
            DayRolloverCompleted?.Invoke();
        }
    }

    private async ValueTask NotifyDayRolloverStartingAsync(CancellationToken cancellationToken)
    {
        Func<CancellationToken, ValueTask>? handlers = DayRolloverStarting;
        if (handlers is null)
        {
            return;
        }

        foreach (Delegate handler in handlers.GetInvocationList())
        {
            await ((Func<CancellationToken, ValueTask>)handler)(cancellationToken).ConfigureAwait(false);
        }
    }

    private void FlushAll()
    {
        ConsumerStateStore[] stores = SnapshotStates();
        foreach (ConsumerStateStore store in stores)
        {
            try
            {
                store.Flush();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to flush session-log consumer state.");
            }
        }
    }

    private void Changed()
    {
        if (_options.FlushMode == SessionLogFlushMode.PerMessage)
        {
            _flushSignal.Writer.TryWrite(0);
        }
    }

    private SessionLogStream[] SnapshotStreams()
    {
        lock (_gate)
        {
            return _streams.Values.ToArray();
        }
    }

    private ConsumerStateStore[] SnapshotStates()
    {
        lock (_gate)
        {
            return _states.ToArray();
        }
    }

    private ushort AssignId(string sessionId)
    {
        if (_catalog.Sessions.TryGetValue(sessionId, out ushort existing))
        {
            return existing;
        }

        if (_catalog.Next is < 1 or > ushort.MaxValue)
        {
            throw new InvalidOperationException("The session log has no stream ids left.");
        }

        ushort id = (ushort)_catalog.Next;
        _catalog.Next++;
        _catalog.Sessions[sessionId] = id;
        return id;
    }

    private void LoadCatalog()
    {
        string path = CatalogPath();
        if (!File.Exists(path))
        {
            _catalog = new Catalog();
            return;
        }

        _catalog = JsonSerializer.Deserialize<Catalog>(File.ReadAllText(path), CatalogJson) ?? new Catalog();
        if (_catalog.Sessions is null)
        {
            _catalog.Sessions = new Dictionary<string, ushort>(StringComparer.Ordinal);
        }

        if (_catalog.Next < 1)
        {
            _catalog.Next = 1;
        }
    }

    private void SaveCatalog()
    {
        string path = CatalogPath();
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(_catalog, CatalogJson));
        File.Move(temporary, path, overwrite: true);
    }

    private string CatalogPath() => SessionLogPaths.Under(_options.Directory, "streams", "catalog.json");

    private string CleanPath() => SessionLogPaths.Under(_options.Directory, "clean");

    private void DisposeMaps()
    {
        lock (_gate)
        {
            foreach (SessionLogStream stream in _streams.Values)
            {
                stream.Dispose();
            }

            foreach (ClientBinding binding in _clients.Values)
            {
                if (binding.Index is IDisposable disposable)
                {
                    disposable.Dispose();
                }
            }

            _streams.Clear();
            _byId.Clear();
        }
    }

    private static async Task WaitAsync(Task? task, CancellationToken cancellationToken)
    {
        if (task is null)
        {
            return;
        }

        try
        {
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
        }
    }

    private int CurrentTradingDay()
    {
        DateTime local = TimeZoneInfo.ConvertTime(_timeProvider.GetUtcNow(), _rolloverTimeZone).DateTime;
        return TradingDay(local);
    }

    private static DateOnly TradingDate(int tradingDay) =>
        DateOnly.ParseExact(
            tradingDay.ToString("D8", System.Globalization.CultureInfo.InvariantCulture),
            "yyyyMMdd",
            System.Globalization.CultureInfo.InvariantCulture);

    private static int TradingDay(DateTime local) => local.Year * 10000 + local.Month * 100 + local.Day;

    private sealed class Catalog
    {
        public int Next { get; set; } = 1;

        public Dictionary<string, ushort> Sessions { get; set; } = new(StringComparer.Ordinal);
    }
}
