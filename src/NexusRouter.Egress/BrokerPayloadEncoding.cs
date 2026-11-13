namespace NexusRouter.Egress;

/// <summary>
/// Formato do corpo que um dispatcher de broker publica.
/// </summary>
/// <remarks>
/// O log de sessão e o cursor continuam sobre o frame FIX bruto. Só a mensagem enviada ao broker muda.
/// </remarks>
public enum BrokerPayloadEncoding
{
    /// <summary>
    /// Envelope JSON com <c>type</c>, <c>msg_type</c>, <c>ts</c> e <c>fields</c> (nome FIX em snake_case para valor). É o default.
    /// </summary>
    Json = 0,

    /// <summary>
    /// Frame FIX 4.4 bruto, com SOH entre os campos.
    /// </summary>
    Raw = 1,
}
