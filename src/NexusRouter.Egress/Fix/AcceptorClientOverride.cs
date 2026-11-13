namespace NexusRouter.Egress.Fix;

/// <summary>
/// Sobrescreve a política de recuperação de um SenderCompID. Campo nulo herda o dispatcher.
/// </summary>
public sealed class AcceptorClientOverride
{
    /// <summary>
    /// SenderCompID (tag 49) do cliente.
    /// </summary>
    public required string SenderCompId { get; init; }

    /// <summary>
    /// Substitui <see cref="AcceptorRecoveryOptions.OnReconnectGap"/> quando preenchido.
    /// </summary>
    public OnReconnectGap? OnReconnectGap { get; init; }

    /// <summary>
    /// Substitui <see cref="AcceptorRecoveryOptions.AllowResetSeqNumFlag"/> quando preenchido.
    /// </summary>
    public bool? AllowResetSeqNumFlag { get; init; }

    /// <summary>
    /// Substitui <see cref="AcceptorRecoveryOptions.AcceptNextExpectedMsgSeqNum"/> quando preenchido.
    /// </summary>
    public bool? AcceptNextExpectedMsgSeqNum { get; init; }

    /// <summary>
    /// Substitui <see cref="AcceptorRecoveryOptions.RequireNextExpectedMsgSeqNum"/> quando preenchido.
    /// </summary>
    public bool? RequireNextExpectedMsgSeqNum { get; init; }

    /// <summary>
    /// Substitui <see cref="AcceptorRecoveryOptions.ReplayMode"/> quando preenchido.
    /// </summary>
    public ReplayMode? ReplayMode { get; init; }

    /// <summary>
    /// Substitui <see cref="AcceptorRecoveryOptions.LogoutTimeoutSeconds"/> quando preenchido.
    /// </summary>
    public int? LogoutTimeoutSeconds { get; init; }
}
