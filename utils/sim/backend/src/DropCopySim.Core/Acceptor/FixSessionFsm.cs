namespace DropCopySim.Core.Acceptor;

/// <summary>
/// Ciclo de vida da sessão FIX do acceptor e relógio de heartbeat, sem I/O.
/// </summary>
/// <remarks>
/// O caminho feliz é <see cref="AcceptorSessionState.Disconnected"/> → <see cref="AcceptorSessionState.Connected"/> →
/// <see cref="AcceptorSessionState.LogonReceived"/> → <see cref="AcceptorSessionState.Active"/> →
/// <see cref="AcceptorSessionState.LogoutSent"/> → <see cref="AcceptorSessionState.Disconnected"/>.
/// Uma queda de transporte pode entrar em <see cref="AcceptorSessionState.Disconnected"/> a partir de
/// <see cref="AcceptorSessionState.Connected"/>, <see cref="AcceptorSessionState.LogonReceived"/>,
/// <see cref="AcceptorSessionState.Active"/> ou <see cref="AcceptorSessionState.LogoutSent"/>.
/// As trocas de estado usam <see cref="Interlocked.CompareExchange(ref int, int, int)"/>.
/// Qualquer mensagem recebida reinicia o relógio de heartbeat.
/// </remarks>
public sealed class FixSessionFsm
{
    private readonly TimeProvider _timeProvider;
    private int _state = (int)AcceptorSessionState.Disconnected;
    private long _lastActivityTicks;

    /// <summary>
    /// Cria a máquina de estados usando o provedor de tempo informado ou o relógio do sistema.
    /// </summary>
    /// <param name="timeProvider">Relógio monotônico usado para medir o silêncio da sessão.</param>
    public FixSessionFsm(TimeProvider? timeProvider = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _lastActivityTicks = _timeProvider.GetTimestamp();
    }

    /// <summary>Estado atual da sessão.</summary>
    public AcceptorSessionState State => (AcceptorSessionState)Volatile.Read(ref _state);

    /// <summary>
    /// Passa de <see cref="AcceptorSessionState.Disconnected"/> para <see cref="AcceptorSessionState.Connected"/>.
    /// </summary>
    /// <exception cref="InvalidSessionStateException">O estado atual não é <see cref="AcceptorSessionState.Disconnected"/>.</exception>
    public void TransitionToConnected() =>
        Transition(AcceptorSessionState.Disconnected, AcceptorSessionState.Connected);

    /// <summary>
    /// Passa de <see cref="AcceptorSessionState.Connected"/> para <see cref="AcceptorSessionState.LogonReceived"/>.
    /// </summary>
    /// <exception cref="InvalidSessionStateException">O estado atual não é <see cref="AcceptorSessionState.Connected"/>.</exception>
    public void TransitionToLogonReceived() =>
        Transition(AcceptorSessionState.Connected, AcceptorSessionState.LogonReceived);

    /// <summary>
    /// Passa de <see cref="AcceptorSessionState.LogonReceived"/> para <see cref="AcceptorSessionState.Active"/> e reinicia o relógio de heartbeat.
    /// </summary>
    /// <exception cref="InvalidSessionStateException">O estado atual não é <see cref="AcceptorSessionState.LogonReceived"/>.</exception>
    public void TransitionToActive()
    {
        Transition(AcceptorSessionState.LogonReceived, AcceptorSessionState.Active);
        RecordInboundMessage();
    }

    /// <summary>
    /// Passa de <see cref="AcceptorSessionState.Active"/> para <see cref="AcceptorSessionState.LogoutSent"/>.
    /// </summary>
    /// <exception cref="InvalidSessionStateException">O estado atual não é <see cref="AcceptorSessionState.Active"/>.</exception>
    public void TransitionToLogoutSent() =>
        Transition(AcceptorSessionState.Active, AcceptorSessionState.LogoutSent);

    /// <summary>
    /// Encerra a sessão em <see cref="AcceptorSessionState.Disconnected"/>.
    /// </summary>
    /// <remarks>
    /// Predecessores legais: <see cref="AcceptorSessionState.Connected"/>, <see cref="AcceptorSessionState.LogonReceived"/>,
    /// <see cref="AcceptorSessionState.Active"/> e <see cref="AcceptorSessionState.LogoutSent"/>.
    /// O logout gracioso usa <see cref="AcceptorSessionState.LogoutSent"/>; os demais cobrem queda de transporte.
    /// </remarks>
    /// <exception cref="InvalidSessionStateException">A sessão já está <see cref="AcceptorSessionState.Disconnected"/>.</exception>
    public void TransitionToDisconnected()
    {
        var observed = Volatile.Read(ref _state);
        while (true)
        {
            if (!CanDisconnect(observed))
            {
                throw new InvalidSessionStateException((AcceptorSessionState)observed, AcceptorSessionState.Disconnected);
            }

            var previous = Interlocked.CompareExchange(ref _state, (int)AcceptorSessionState.Disconnected, observed);
            if (previous == observed)
            {
                return;
            }

            observed = previous;
        }
    }

    /// <summary>
    /// Indica se o silêncio de entrada já cobre o número configurado de intervalos de heartbeat.
    /// </summary>
    /// <param name="missedHeartbeatThreshold">Quantidade de heartbeats consecutivos ausentes que declara a sessão inativa.</param>
    /// <param name="heartbeatIntervalSeconds">HeartBtInt da sessão, em segundos.</param>
    /// <returns><see langword="true"/> quando o tempo desde a última mensagem recebida atinge o limite.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// <paramref name="missedHeartbeatThreshold"/> ou <paramref name="heartbeatIntervalSeconds"/> não é positivo.
    /// </exception>
    /// <remarks>
    /// A leitura do relógio não usa <c>lock</c>. O limite é
    /// <c>missedHeartbeatThreshold × heartbeatIntervalSeconds</c> segundos, medido com
    /// <see cref="TimeProvider.GetTimestamp"/> e <see cref="TimeProvider.GetElapsedTime(long)"/>.
    /// </remarks>
    public bool IsHeartbeatStale(int missedHeartbeatThreshold, int heartbeatIntervalSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(missedHeartbeatThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heartbeatIntervalSeconds);

        var lastTicks = Volatile.Read(ref _lastActivityTicks);
        double elapsedMilliseconds = _timeProvider.GetElapsedTime(lastTicks).TotalMilliseconds;
        if (elapsedMilliseconds < 0d)
        {
            return false;
        }

        double limitMilliseconds = (double)missedHeartbeatThreshold * heartbeatIntervalSeconds * 1000d;
        return elapsedMilliseconds >= limitMilliseconds;
    }

    /// <summary>
    /// Reinicia o relógio de heartbeat com o instante atual.
    /// </summary>
    /// <remarks>
    /// A spec FIX trata qualquer mensagem recebida como evidência de vida da sessão, inclusive mensagens de aplicação.
    /// </remarks>
    public void RecordInboundMessage() =>
        Interlocked.Exchange(ref _lastActivityTicks, _timeProvider.GetTimestamp());

    private void Transition(AcceptorSessionState required, AcceptorSessionState next)
    {
        var observed = Interlocked.CompareExchange(ref _state, (int)next, (int)required);
        if (observed != (int)required)
        {
            throw new InvalidSessionStateException((AcceptorSessionState)observed, next, required);
        }
    }

    private static bool CanDisconnect(int state) =>
        state is (int)AcceptorSessionState.Connected
            or (int)AcceptorSessionState.LogonReceived
            or (int)AcceptorSessionState.Active
            or (int)AcceptorSessionState.LogoutSent;
}
