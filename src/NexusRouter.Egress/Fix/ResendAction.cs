using NexusRouter.Domain.Models;

namespace NexusRouter.Egress.Fix;

/// <summary>
/// O que o <see cref="ResendPlanner"/> manda a sessão fazer.
/// </summary>
internal enum ResendActionKind
{
    /// <summary>
    /// Reenviar a mensagem de aplicação do slot, com <c>PossDupFlag=Y</c>.
    /// </summary>
    Application = 0,

    /// <summary>
    /// Enviar um <c>SequenceReset-GapFill</c> que cobre o intervalo.
    /// </summary>
    GapFill = 1,

    /// <summary>
    /// O slot foi reservado e ainda não foi publicado. Esperar um pouco e pedir a ação de novo.
    /// </summary>
    Wait = 2,
}

/// <summary>
/// Uma ação do plano de reenvio.
/// </summary>
/// <param name="Kind">Tipo da ação.</param>
/// <param name="FromSeq">Primeiro seq de saída coberto. É o <c>MsgSeqNum</c> do gap-fill.</param>
/// <param name="ToSeq">Seq seguinte ao último coberto. É o <c>NewSeqNo</c> do gap-fill.</param>
/// <param name="Entry">Slot do índice quando <paramref name="Kind"/> é <see cref="ResendActionKind.Application"/>.</param>
/// <param name="PossMissingApplMsg">Verdadeiro no gap-fill que fecha um pedido acima do limite de mensagens.</param>
internal readonly record struct ResendAction(
    ResendActionKind Kind,
    long FromSeq,
    long ToSeq,
    IndexEntry Entry,
    bool PossMissingApplMsg);
