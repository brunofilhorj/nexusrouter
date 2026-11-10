using NexusRouter.Domain.Abstractions;

namespace NexusRouter.Egress.Fix;

/// <summary>
/// O que a sessão aceita faz depois do Logon já autenticado.
/// </summary>
internal sealed class AcceptorSessionPlan
{
    /// <summary>
    /// HeartBtInt adotado do cliente, em segundos.
    /// </summary>
    internal int HeartBtInt { get; init; } = 20;

    /// <summary>
    /// Início do <c>SequenceReset-GapFill</c> do modo <see cref="OnReconnectGap.SkipToCurrent"/>. Zero não envia.
    /// </summary>
    internal long SkipFrom { get; init; }

    /// <summary>
    /// Quando <see langword="true"/>, um gap-fill cobre só o seq do Logon de resposta (<c>34 = M</c>, <c>NewSeqNo = M+1</c>).
    /// </summary>
    internal bool GapFillLogonSequence { get; init; }

    /// <summary>
    /// Texto do Logout quando o Logon foi recusado. Nulo aceita a sessão.
    /// </summary>
    internal string? RejectText { get; init; }

    /// <summary>
    /// Espera do Logout de confirmação, em segundos.
    /// </summary>
    internal int LogoutTimeoutSeconds { get; init; } = 5;

    /// <summary>
    /// Estado persistido do cliente. <see cref="IConsumerState.NextInExpected"/> é a política mista.
    /// </summary>
    internal required IConsumerState State { get; init; }
}
