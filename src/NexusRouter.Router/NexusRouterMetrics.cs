using System.Diagnostics.Metrics;

using NexusRouter.Domain.Abstractions;

namespace NexusRouter.Router;

/// <summary>
/// Instrumentação centralizada do NexusRouter via <see cref="System.Diagnostics.Metrics"/>.
/// </summary>
/// <remarks>
/// Todos os instrumentos são criados uma única vez na inicialização.
/// O <see cref="ObservableGauge{T}"/> de profundidade de channel sonda os dispatchers
/// via callback; o callback não aloca e não bloqueia o hot path.
/// Seguro para chamada no hot path — zero alocação em regime estacionário.
/// </remarks>
public sealed class NexusRouterMetrics : IDisposable
{
    /// <summary>Nome do <see cref="Meter"/> publicado para o <c>dotnet-counters</c>.</summary>
    public const string MeterName = "NexusRouter";

    private readonly Meter _meter;

    // ── Contadores ──────────────────────────────────────────────────────────

    /// <summary>Total de mensagens recebidas das sessões de ingress, por SessionId.</summary>
    public Counter<long> MessagesReceived { get; }

    /// <summary>Total de mensagens despachadas com sucesso, por DispatcherId.</summary>
    public Counter<long> MessagesDispatched { get; }

    /// <summary>Total de mensagens descartadas por ausência de rota, com tag de motivo.</summary>
    public Counter<long> MessagesDropped { get; }

    /// <summary>Vezes em que o fan-out liberou o buffer e deixou a mensagem no log.</summary>
    public Counter<long> DispatcherLagSignaled { get; }

    // ── Histograma ───────────────────────────────────────────────────────────

    /// <summary>
    /// Latência do hot path em microssegundos: do timestamp de ingress ao fan-out completo.
    /// </summary>
    /// <remarks>
    /// Instrumentado em <see cref="RouterEngine.Route"/>. Calculado como
    /// a diferença entre dois timestamps de <see cref="System.Diagnostics.Stopwatch"/> convertida para microssegundos.
    /// </remarks>
    public Histogram<double> HotPathLatencyMicroseconds { get; }

    // ── Gauge observável ────────────────────────────────────────────────────

    // Referência mantida para evitar GC do instrumento observável.
    private readonly ObservableGauge<int> _channelDepthGauge;
    private readonly ObservableGauge<long> _lagGauge;

    // Lista de callbacks para cada dispatcher registrado.
    private readonly List<(string DispatcherId, Func<int> GetDepth)> _depthCallbacks = [];
    private readonly List<(string DispatcherId, Func<long> GetLag)> _lagCallbacks = [];

    /// <summary>
    /// Cria o <see cref="Meter"/> e registra todos os instrumentos.
    /// </summary>
    public NexusRouterMetrics()
    {
        _meter = new Meter(MeterName, "1.0.0");

        MessagesReceived = _meter.CreateCounter<long>(
            "messages_received_total",
            unit: "{messages}",
            description: "Total de mensagens recebidas das sessões de ingress.");

        MessagesDispatched = _meter.CreateCounter<long>(
            "messages_dispatched_total",
            unit: "{messages}",
            description: "Total de mensagens despachadas com sucesso.");

        MessagesDropped = _meter.CreateCounter<long>(
            "messages_dropped_total",
            unit: "{messages}",
            description: "Total de mensagens descartadas por ausência de rota.");

        DispatcherLagSignaled = _meter.CreateCounter<long>(
            "dispatcher_lag_signaled_total",
            unit: "{messages}",
            description: "Mensagens que ficaram no log porque a rota não tinha dispatcher ou o channel recusou.");

        _lagGauge = _meter.CreateObservableGauge<long>(
            "dispatcher_lag",
            observeValues: ObserveLag,
            unit: "{messages}",
            description: "Distância entre o seq committed do stream e o próximo seq que o dispatcher ainda vai entregar.");

        HotPathLatencyMicroseconds = _meter.CreateHistogram<double>(
            "hotpath_latency_microseconds",
            unit: "us",
            description: "Latência do hot path do ingress ao fan-out, em microssegundos.");

        // O gauge faz polling de todos os callbacks registrados — um por dispatcher.
        _channelDepthGauge = _meter.CreateObservableGauge<int>(
            "dispatcher_channel_depth",
            observeValues: ObserveChannelDepths,
            unit: "{messages}",
            description: "Profundidade atual do channel por dispatcher.");
    }

    /// <summary>
    /// Registra um callback de profundidade de channel para um dispatcher.
    /// </summary>
    /// <param name="dispatcherId">Identificador único do dispatcher.</param>
    /// <param name="getDepth">Função pura que retorna a profundidade atual do channel.</param>
    /// <remarks>
    /// Deve ser chamado na inicialização, antes de qualquer mensagem fluir.
    /// O callback não deve alocar; normalmente retorna um campo lido por <see cref="System.Threading.Volatile"/>.
    /// </remarks>
    public void RegisterChannelDepth(string dispatcherId, Func<int> getDepth)
    {
        ArgumentException.ThrowIfNullOrEmpty(dispatcherId);
        ArgumentNullException.ThrowIfNull(getDepth);
        _depthCallbacks.Add((dispatcherId, getDepth));
    }

    /// <summary>
    /// Registra a distância entre <c>committed</c> e o cursor do dispatcher.
    /// </summary>
    /// <param name="dispatcherId">Identificador único do dispatcher.</param>
    /// <param name="getLag">Função que devolve quantas mensagens o dispatcher ainda não entregou.</param>
    public void RegisterLag(string dispatcherId, Func<long> getLag)
    {
        ArgumentException.ThrowIfNullOrEmpty(dispatcherId);
        ArgumentNullException.ThrowIfNull(getLag);
        _lagCallbacks.Add((dispatcherId, getLag));
    }

    /// <inheritdoc/>
    public void Dispose() => _meter.Dispose();

    // ── Internos ─────────────────────────────────────────────────────────────

    private IEnumerable<Measurement<int>> ObserveChannelDepths()
    {
        foreach ((string id, Func<int> get) in _depthCallbacks)
        {
            yield return new Measurement<int>(get(), new KeyValuePair<string, object?>("dispatcher_id", id));
        }
    }

    private IEnumerable<Measurement<long>> ObserveLag()
    {
        foreach ((string id, Func<long> get) in _lagCallbacks)
        {
            yield return new Measurement<long>(get(), new KeyValuePair<string, object?>("dispatcher_id", id));
        }
    }
}
