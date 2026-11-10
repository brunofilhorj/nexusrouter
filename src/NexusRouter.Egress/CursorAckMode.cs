namespace NexusRouter.Egress;

/// <summary>
/// Quando o cursor durável de um dispatcher de broker avança.
/// </summary>
public enum CursorAckMode
{
    /// <summary>
    /// Avança na confirmação do broker. A entrega é ao menos uma vez.
    /// </summary>
    OnBrokerAck = 0,

    /// <summary>
    /// Avança ao entregar a mensagem à biblioteca cliente. A entrega é no máximo uma vez.
    /// </summary>
    OnEnqueue = 1,
}
