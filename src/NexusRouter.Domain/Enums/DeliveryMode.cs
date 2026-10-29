namespace NexusRouter.Domain.Enums;

/// <summary>
/// Define como uma sessão entrega a mensagem inbound aos dispatchers.
/// </summary>
public enum DeliveryMode
{
    /// <summary>
    /// Encaminha o buffer FIX bruto. Apenas MsgSeqNum e CheckSum são reescritos no lugar.
    /// </summary>
    PassThrough = 0,

    /// <summary>
    /// Tokeniza os campos FIX antes do handoff aos dispatchers.
    /// </summary>
    Parse = 1,
}
