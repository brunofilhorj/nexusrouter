namespace NexusRouter.Egress.Fix;

/// <summary>
/// Política de sessão do acceptor (design §3.4.7). Vale para o dispatcher e pode ser fundida com um <see cref="AcceptorClientOverride"/>.
/// </summary>
public sealed class AcceptorRecoveryOptions
{
    /// <summary>
    /// Comportamento quando o cliente está atrás do próximo seq de saída.
    /// </summary>
    public OnReconnectGap OnReconnectGap { get; init; } = OnReconnectGap.WaitForResendRequest;

    /// <summary>
    /// Quando <see langword="true"/>, Logon com <c>141=Y</c> abre uma época nova do cliente.
    /// </summary>
    public bool AllowResetSeqNumFlag { get; init; } = true;

    /// <summary>
    /// Quando <see langword="true"/>, a tag <c>789</c> do Logon do cliente é lida.
    /// </summary>
    public bool AcceptNextExpectedMsgSeqNum { get; init; } = true;

    /// <summary>
    /// Quando <see langword="true"/>, Logon sem a tag <c>789</c> é recusado.
    /// </summary>
    public bool RequireNextExpectedMsgSeqNum { get; init; }

    /// <summary>
    /// Modo de entrega aplicado quando um Logon abre uma época do cliente.
    /// </summary>
    public ReplayMode ReplayMode { get; init; } = ReplayMode.FromLogon;

    /// <summary>
    /// Segundos de espera pelo Logout de confirmação antes de fechar o socket.
    /// </summary>
    public int LogoutTimeoutSeconds { get; init; } = 5;

    /// <summary>
    /// Devolve a política do dispatcher com os campos preenchidos do override.
    /// </summary>
    /// <param name="client">Override de um SenderCompID. Campos nulos herdam o dispatcher.</param>
    /// <returns>Política efetiva daquele cliente.</returns>
    public AcceptorRecoveryOptions Merge(AcceptorClientOverride client)
    {
        ArgumentNullException.ThrowIfNull(client);
        return new AcceptorRecoveryOptions
        {
            OnReconnectGap = client.OnReconnectGap ?? OnReconnectGap,
            AllowResetSeqNumFlag = client.AllowResetSeqNumFlag ?? AllowResetSeqNumFlag,
            AcceptNextExpectedMsgSeqNum = client.AcceptNextExpectedMsgSeqNum ?? AcceptNextExpectedMsgSeqNum,
            RequireNextExpectedMsgSeqNum = client.RequireNextExpectedMsgSeqNum ?? RequireNextExpectedMsgSeqNum,
            ReplayMode = client.ReplayMode ?? ReplayMode,
            LogoutTimeoutSeconds = client.LogoutTimeoutSeconds ?? LogoutTimeoutSeconds,
        };
    }

    /// <summary>
    /// Rejeita combinação inválida ou timeout não positivo.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException"><see cref="LogoutTimeoutSeconds"/> não é positivo.</exception>
    /// <exception cref="InvalidOperationException"><see cref="RequireNextExpectedMsgSeqNum"/> está ligado e <see cref="AcceptNextExpectedMsgSeqNum"/> desligado, ou um modo não existe.</exception>
    public void EnsureValid()
    {
        if (!Enum.IsDefined(OnReconnectGap))
        {
            throw new InvalidOperationException("Recovery.OnReconnectGap não é um modo conhecido.");
        }

        if (!Enum.IsDefined(ReplayMode))
        {
            throw new InvalidOperationException("Recovery.ReplayMode não é um modo conhecido.");
        }

        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(LogoutTimeoutSeconds);
        if (RequireNextExpectedMsgSeqNum && !AcceptNextExpectedMsgSeqNum)
        {
            throw new InvalidOperationException(
                "Recovery.RequireNextExpectedMsgSeqNum exige Recovery.AcceptNextExpectedMsgSeqNum.");
        }
    }
}
