using DropCopySim.Core.Telemetry;

namespace DropCopySim.Core.Generation;

/// <summary>
/// Dono da máquina de estados da rodada (<see cref="SimulationState.Idle"/> /
/// <see cref="SimulationState.Running"/> / <see cref="SimulationState.Paused"/>) e dos contadores.
/// Comanda o <see cref="OrderFlowGenerator"/> e valida as transições (FR-SIM-01, FR-SIM-02).
/// </summary>
/// <remarks>
/// Transições ilegais lançam <see cref="InvalidSimulationStateException"/> (traduzida pela API em HTTP 409).
/// A reconfiguração segue a política híbrida por campo (FR-SIM-06, FR-SIM-08): em
/// <see cref="SimulationState.Idle"/>/<see cref="SimulationState.Paused"/> aplica qualquer campo; em
/// <see cref="SimulationState.Running"/> aceita um delta que toque apenas campos hot
/// (<see cref="SimulationConfig.RatePerSecond"/>, <see cref="SimulationConfig.Outcome"/>) e rejeita por
/// inteiro — tudo-ou-nada — qualquer delta que toque um campo travado. A config vigente é publicada como
/// snapshot imutável via <see cref="OrderFlowGenerator.PublishConfig"/>, nunca mutada em instância.
/// As transições são emitidas na telemetria como <see cref="SimulationStateChanged"/> pelo gerador.
/// </remarks>
public sealed class SimulationController : IAsyncDisposable
{
    private readonly OrderFlowGenerator _generator;
    private readonly SimulationCounters _counters;
    private readonly object _gate = new();

    private SimulationConfig _config;

    /// <summary>
    /// Cria o controlador ligado a um gerador e à config inicial.
    /// </summary>
    /// <param name="generator">Gerador de fluxo de ordens que executa a rodada.</param>
    /// <param name="counters">Contadores da rodada, zerados ao parar (Stop).</param>
    /// <param name="initialConfig">Snapshot imutável inicial da configuração da simulação.</param>
    /// <exception cref="ArgumentNullException">Um argumento obrigatório é nulo.</exception>
    public SimulationController(
        OrderFlowGenerator generator,
        SimulationCounters counters,
        SimulationConfig initialConfig)
    {
        ArgumentNullException.ThrowIfNull(generator);
        ArgumentNullException.ThrowIfNull(counters);
        ArgumentNullException.ThrowIfNull(initialConfig);

        _generator = generator;
        _counters = counters;
        _config = initialConfig;
        _generator.PublishConfig(initialConfig);
    }

    /// <summary>Estado atual da máquina de estados de controle.</summary>
    public SimulationState State => _generator.State;

    /// <summary>Snapshot imutável da configuração vigente.</summary>
    public SimulationConfig Config => Volatile.Read(ref _config);

    /// <summary>Contadores da rodada ativa ou mais recente.</summary>
    public SimulationCounters Counters => _counters;

    /// <summary>
    /// Inicia a geração (<see cref="SimulationState.Idle"/> → <see cref="SimulationState.Running"/>).
    /// </summary>
    /// <exception cref="InvalidSimulationStateException">A simulação não está em <see cref="SimulationState.Idle"/>.</exception>
    /// <exception cref="ObjectDisposedException">O controlador foi descartado.</exception>
    public void Start()
    {
        lock (_gate)
        {
            try
            {
                _generator.Start();
            }
            catch (InvalidOperationException ex)
            {
                throw new InvalidSimulationStateException(State, nameof(Start), ex.Message);
            }
        }
    }

    /// <summary>
    /// Suspende a geração (<see cref="SimulationState.Running"/> → <see cref="SimulationState.Paused"/>),
    /// preservando estado e contadores.
    /// </summary>
    /// <exception cref="InvalidSimulationStateException">A simulação não está em <see cref="SimulationState.Running"/>.</exception>
    /// <exception cref="ObjectDisposedException">O controlador foi descartado.</exception>
    public void Pause()
    {
        lock (_gate)
        {
            try
            {
                _generator.Pause();
            }
            catch (InvalidOperationException)
            {
                throw new InvalidSimulationStateException(State, nameof(Pause));
            }
        }
    }

    /// <summary>
    /// Retoma a geração (<see cref="SimulationState.Paused"/> → <see cref="SimulationState.Running"/>).
    /// </summary>
    /// <exception cref="InvalidSimulationStateException">A simulação não está em <see cref="SimulationState.Paused"/>.</exception>
    /// <exception cref="ObjectDisposedException">O controlador foi descartado.</exception>
    public void Resume()
    {
        lock (_gate)
        {
            try
            {
                _generator.Resume();
            }
            catch (InvalidOperationException)
            {
                throw new InvalidSimulationStateException(State, nameof(Resume));
            }
        }
    }

    /// <summary>
    /// Encerra a geração (<see cref="SimulationState.Running"/>/<see cref="SimulationState.Paused"/> →
    /// <see cref="SimulationState.Idle"/>) e zera o estado da rodada.
    /// </summary>
    /// <returns>Uma task que completa quando o loop de background encerrou e os contadores foram zerados.</returns>
    /// <exception cref="InvalidSimulationStateException">A simulação já está em <see cref="SimulationState.Idle"/>.</exception>
    /// <exception cref="ObjectDisposedException">O controlador foi descartado.</exception>
    public async Task StopAsync()
    {
        Task stopping;
        lock (_gate)
        {
            if (State == SimulationState.Idle)
            {
                throw new InvalidSimulationStateException(State, nameof(StopAsync));
            }

            try
            {
                stopping = _generator.StopAsync();
            }
            catch (InvalidOperationException)
            {
                throw new InvalidSimulationStateException(State, nameof(StopAsync));
            }
        }

        await stopping.ConfigureAwait(false);
        _counters.Reset();
    }

    /// <summary>
    /// Aplica uma nova configuração segundo a política híbrida por campo.
    /// Em <see cref="SimulationState.Idle"/>/<see cref="SimulationState.Paused"/> qualquer campo é aceito;
    /// em <see cref="SimulationState.Running"/> apenas um delta que toque campos hot
    /// (<see cref="SimulationConfig.RatePerSecond"/>, <see cref="SimulationConfig.Outcome"/>) é aceito, e um
    /// delta que toque qualquer campo travado é rejeitado por inteiro (tudo-ou-nada).
    /// </summary>
    /// <param name="candidate">Snapshot imutável candidato. A instância vigente nunca é mutada.</param>
    /// <exception cref="ArgumentNullException"><paramref name="candidate"/> é nulo.</exception>
    /// <exception cref="InvalidSimulationStateException">
    /// O candidato toca um campo travado enquanto a simulação está em <see cref="SimulationState.Running"/>.
    /// </exception>
    /// <exception cref="ObjectDisposedException">O controlador foi descartado.</exception>
    public void UpdateConfig(SimulationConfig candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        lock (_gate)
        {
            var current = Volatile.Read(ref _config);
            if (State == SimulationState.Running && LockedFieldsDiffer(current, candidate))
            {
                throw new InvalidSimulationStateException(
                    State,
                    nameof(UpdateConfig),
                    "Reconfiguração rejeitada: o corpo toca campos travados enquanto a simulação está em Running. "
                    + "Em Running, apenas RatePerSecond e Outcome podem mudar (tudo-ou-nada).");
            }

            Volatile.Write(ref _config, candidate);
            _generator.PublishConfig(candidate);
        }
    }

    /// <summary>Encaminha o descarte ao gerador, encerrando o loop se ainda estiver ativo.</summary>
    /// <returns>Uma task que completa quando o loop de background encerrou.</returns>
    public ValueTask DisposeAsync() => _generator.DisposeAsync();

    /// <summary>
    /// Indica se algum campo travado difere entre a config vigente e o candidato.
    /// Os campos hot (<see cref="SimulationConfig.RatePerSecond"/>, <see cref="SimulationConfig.Outcome"/>)
    /// são deliberadamente ignorados.
    /// </summary>
    private static bool LockedFieldsDiffer(SimulationConfig current, SimulationConfig candidate)
    {
        return current.TotalOrders != candidate.TotalOrders
            || current.AccountSelection != candidate.AccountSelection
            || current.PartialFillsMax != candidate.PartialFillsMax
            || current.TestMessageIndicator != candidate.TestMessageIndicator
            || !SequenceEqual(current.Accounts, candidate.Accounts, StringComparer.Ordinal)
            || !SequenceEqual(current.EnabledMessageTypes, candidate.EnabledMessageTypes, StringComparer.Ordinal)
            || !SequenceEqual(current.Instruments, candidate.Instruments, EqualityComparer<Instrument>.Default);
    }

    private static bool SequenceEqual<T>(
        IReadOnlyList<T> left,
        IReadOnlyList<T> right,
        IEqualityComparer<T> comparer)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left.Count != right.Count)
        {
            return false;
        }

        for (var i = 0; i < left.Count; i++)
        {
            if (!comparer.Equals(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }
}
