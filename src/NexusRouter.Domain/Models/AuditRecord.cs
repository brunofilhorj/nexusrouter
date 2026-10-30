using NexusRouter.Domain.Enums;

namespace NexusRouter.Domain.Models;

/// <summary>
/// Registro de auditoria de uma mensagem, de um descarte ou de um evento de ciclo de vida.
/// </summary>
/// <remarks>
/// <see cref="RawFix"/> é preenchido no enqueue do cold path, por cópia para um array do próprio registro
/// ou alugado de <see cref="System.Buffers.ArrayPool{T}"/>. O router não faz I/O para produzir este registro.
/// Quando <see cref="LifecycleEvent"/> está definido, os campos de roteamento e despacho ficam sem valor:
/// <see cref="MsgType"/>, <see cref="ClOrdId"/>, <see cref="MsgSeqNum"/>, <see cref="DropReason"/> e <see cref="RawFix"/>
/// são nulos, e <see cref="DispatcherIds"/> permanece vazio.
/// </remarks>
public sealed class AuditRecord
{
    /// <summary>
    /// Sessão que originou o evento.
    /// </summary>
    public SessionId SessionId { get; set; }

    /// <summary>
    /// Instante de ingresso, em ticks do relógio usado pela sessão.
    /// </summary>
    public long IngressTimestampTicks { get; set; }

    /// <summary>
    /// MsgType (tag 35), quando conhecido.
    /// </summary>
    public string? MsgType { get; set; }

    /// <summary>
    /// ClOrdID (tag 11), quando a mensagem contém o campo.
    /// </summary>
    public string? ClOrdId { get; set; }

    /// <summary>
    /// MsgSeqNum (tag 34), quando conhecido.
    /// </summary>
    public long? MsgSeqNum { get; set; }

    /// <summary>
    /// Modo de entrega da rota que tratou a mensagem.
    /// </summary>
    public DeliveryMode DeliveryMode { get; set; }

    /// <summary>
    /// Identificadores dos dispatchers alvo.
    /// </summary>
    public string[] DispatcherIds { get; set; } = [];

    /// <summary>
    /// Instante de egresso, em ticks do mesmo relógio de <see cref="IngressTimestampTicks"/>.
    /// </summary>
    public long EgressTimestampTicks { get; set; }

    /// <summary>
    /// Indica que a mensagem não foi entregue a pelo menos um destino.
    /// </summary>
    public bool WasDropped { get; set; }

    /// <summary>
    /// Motivo do descarte, quando <see cref="WasDropped"/> é <see langword="true"/>.
    /// </summary>
    public string? DropReason { get; set; }

    /// <summary>
    /// Cópia do payload FIX. Nula quando a política de persistência não guarda o bruto.
    /// </summary>
    public byte[]? RawFix { get; set; }

    /// <summary>
    /// Evento de ciclo de vida da sessão. Nulo quando o registro descreve uma mensagem ou um descarte.
    /// </summary>
    /// <remarks>
    /// Com este campo definido, o registro não carrega decisão de roteamento nem resultado de dispatcher.
    /// </remarks>
    public SessionLifecycleEvent? LifecycleEvent { get; set; }

    /// <summary>
    /// Zera o registro para reutilização. Solta <see cref="RawFix"/> sem devolver o array ao pool:
    /// quem alugou o buffer devolve o array antes de chamar este método.
    /// </summary>
    public void Reset()
    {
        SessionId = default;
        IngressTimestampTicks = 0;
        MsgType = null;
        ClOrdId = null;
        MsgSeqNum = null;
        DeliveryMode = default;
        DispatcherIds = [];
        EgressTimestampTicks = 0;
        WasDropped = false;
        DropReason = null;
        RawFix = null;
        LifecycleEvent = null;
    }
}
