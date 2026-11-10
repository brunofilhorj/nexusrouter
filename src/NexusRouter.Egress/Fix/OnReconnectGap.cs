namespace NexusRouter.Egress.Fix;

/// <summary>
/// O que o acceptor faz quando o cliente reconecta atrás do próximo seq de saída.
/// </summary>
public enum OnReconnectGap
{
    /// <summary>
    /// Responde o Logon e espera o <c>ResendRequest</c> do cliente.
    /// </summary>
    WaitForResendRequest = 0,

    /// <summary>
    /// Responde o Logon e envia <c>SequenceReset-GapFill</c> até o seq corrente, sem reenviar aplicação.
    /// </summary>
    SkipToCurrent = 1,
}
