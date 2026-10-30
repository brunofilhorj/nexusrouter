using NexusRouter.Domain.Enums;

namespace NexusRouter.Ingress.Sessions;

/// <summary>
/// Ciclo de vida da sessão FIX e relógio de heartbeat, sem I/O.
/// </summary>
/// <remarks>
/// O caminho feliz é <see cref="SessionState.Disconnected"/> → <see cref="SessionState.Connecting"/> →
/// <see cref="SessionState.LogonSent"/> → <see cref="SessionState.Active"/> →
/// <see cref="SessionState.LogoutSent"/> → <see cref="SessionState.Disconnected"/>.
/// Uma queda de transporte pode entrar em <see cref="SessionState.Disconnected"/> a partir de
/// <see cref="SessionState.Connecting"/>, <see cref="SessionState.LogonSent"/>,
/// <see cref="SessionState.Active"/> ou <see cref="SessionState.LogoutSent"/>.
/// As trocas de estado usam <see cref="Interlocked.CompareExchange(ref int, int, int)"/>.
/// Qualquer mensagem recebida reinicia o relógio de heartbeat.
/// </remarks>
public sealed class FixSessionStateMachine
{
    private static readonly byte[] HeartbeatReply = "35=0\x01"u8.ToArray();
    private static readonly byte[] SequenceResetReply = "35=4\x01"u8.ToArray();
    private static readonly byte[] LogoutReply = "35=5\x01"u8.ToArray();

    private int _state = (int)SessionState.Disconnected;
    private long _lastHeartbeatReceivedTicks = Environment.TickCount64;

    /// <summary>
    /// Estado atual da sessão.
    /// </summary>
    public SessionState State => (SessionState)Volatile.Read(ref _state);

    /// <summary>
    /// Passa de <see cref="SessionState.Disconnected"/> para <see cref="SessionState.Connecting"/>.
    /// </summary>
    /// <exception cref="InvalidSessionStateException">O estado atual não é <see cref="SessionState.Disconnected"/>.</exception>
    public void TransitionToConnecting() =>
        Transition(SessionState.Disconnected, SessionState.Connecting);

    /// <summary>
    /// Passa de <see cref="SessionState.Connecting"/> para <see cref="SessionState.LogonSent"/>.
    /// </summary>
    /// <exception cref="InvalidSessionStateException">O estado atual não é <see cref="SessionState.Connecting"/>.</exception>
    public void TransitionToLogonSent() =>
        Transition(SessionState.Connecting, SessionState.LogonSent);

    /// <summary>
    /// Passa de <see cref="SessionState.LogonSent"/> para <see cref="SessionState.Active"/> e reinicia o relógio de heartbeat.
    /// </summary>
    /// <exception cref="InvalidSessionStateException">O estado atual não é <see cref="SessionState.LogonSent"/>.</exception>
    public void TransitionToActive()
    {
        Transition(SessionState.LogonSent, SessionState.Active);
        RecordInboundMessage();
    }

    /// <summary>
    /// Passa de <see cref="SessionState.Active"/> para <see cref="SessionState.LogoutSent"/>.
    /// </summary>
    /// <exception cref="InvalidSessionStateException">O estado atual não é <see cref="SessionState.Active"/>.</exception>
    public void TransitionToLogoutSent() =>
        Transition(SessionState.Active, SessionState.LogoutSent);

    /// <summary>
    /// Encerra a sessão em <see cref="SessionState.Disconnected"/>.
    /// </summary>
    /// <remarks>
    /// Predecessores legais: <see cref="SessionState.Connecting"/>, <see cref="SessionState.LogonSent"/>,
    /// <see cref="SessionState.Active"/> e <see cref="SessionState.LogoutSent"/>.
    /// O logout gracioso usa <see cref="SessionState.LogoutSent"/>; os demais cobrem queda de transporte.
    /// </remarks>
    /// <exception cref="InvalidSessionStateException">A sessão já está <see cref="SessionState.Disconnected"/>.</exception>
    public void TransitionToDisconnected()
    {
        var observed = Volatile.Read(ref _state);
        while (true)
        {
            if (!CanDisconnect(observed))
            {
                throw new InvalidSessionStateException((SessionState)observed, SessionState.Disconnected);
            }

            var previous = Interlocked.CompareExchange(ref _state, (int)SessionState.Disconnected, observed);
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
    /// <see cref="Environment.TickCount64"/>.
    /// </remarks>
    public bool IsHeartbeatStale(int missedHeartbeatThreshold, int heartbeatIntervalSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(missedHeartbeatThreshold);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(heartbeatIntervalSeconds);

        var lastTicks = Volatile.Read(ref _lastHeartbeatReceivedTicks);
        var elapsedMilliseconds = Environment.TickCount64 - lastTicks;
        if (elapsedMilliseconds < 0L)
        {
            return false;
        }

        var limitMilliseconds = (long)missedHeartbeatThreshold * heartbeatIntervalSeconds * 1000L;
        return elapsedMilliseconds >= limitMilliseconds;
    }

    /// <summary>
    /// Reinicia o relógio de heartbeat com o instante atual.
    /// </summary>
    /// <remarks>
    /// A spec FIX trata qualquer mensagem recebida como evidência de vida da sessão, inclusive mensagens de aplicação.
    /// </remarks>
    public void RecordInboundMessage() =>
        Interlocked.Exchange(ref _lastHeartbeatReceivedTicks, Environment.TickCount64);

    /// <summary>
    /// Registra um Heartbeat recebido. Não há resposta de protocolo.
    /// </summary>
    /// <returns>Buffer vazio. A sessão não envia frame.</returns>
    /// <remarks>Caminho administrativo. O buffer vazio é compartilhado.</remarks>
    public byte[] HandleHeartbeat()
    {
        RecordInboundMessage();
        return [];
    }

    /// <summary>
    /// Registra um TestRequest recebido e devolve o Heartbeat de resposta.
    /// </summary>
    /// <returns>Stub <c>35=0</c> seguido de SOH. A sessão envia esse frame.</returns>
    /// <remarks>
    /// Aloca uma cópia pequena. O frame ainda não traz header, <c>TestReqID</c> nem checksum;
    /// a montagem completa fica na sessão.
    /// </remarks>
    public byte[] HandleTestRequest()
    {
        RecordInboundMessage();
        return Copy(HeartbeatReply);
    }

    /// <summary>
    /// Registra um ResendRequest recebido e devolve um SequenceReset de gap-fill.
    /// </summary>
    /// <returns>Stub <c>35=4</c> seguido de SOH. A sessão envia esse frame.</returns>
    /// <remarks>
    /// Aloca uma cópia pequena. O frame ainda não traz <c>GapFillFlag</c>, intervalo de sequência nem checksum.
    /// </remarks>
    public byte[] HandleResendRequest()
    {
        RecordInboundMessage();
        return Copy(SequenceResetReply);
    }

    /// <summary>
    /// Registra um Logout recebido e devolve o Logout de resposta.
    /// </summary>
    /// <returns>Stub <c>35=5</c> seguido de SOH. A sessão envia esse frame.</returns>
    /// <remarks>
    /// Aloca uma cópia pequena. Não altera o estado: quem chama decide entre
    /// <see cref="TransitionToLogoutSent"/> e <see cref="TransitionToDisconnected"/>.
    /// </remarks>
    public byte[] HandleLogout()
    {
        RecordInboundMessage();
        return Copy(LogoutReply);
    }

    private void Transition(SessionState required, SessionState next)
    {
        var observed = Interlocked.CompareExchange(ref _state, (int)next, (int)required);
        if (observed != (int)required)
        {
            throw new InvalidSessionStateException((SessionState)observed, next, required);
        }
    }

    private static bool CanDisconnect(int state) =>
        state is (int)SessionState.Connecting
            or (int)SessionState.LogonSent
            or (int)SessionState.Active
            or (int)SessionState.LogoutSent;

    private static byte[] Copy(byte[] source)
    {
        var copy = new byte[source.Length];
        source.CopyTo(copy, 0);
        return copy;
    }
}
