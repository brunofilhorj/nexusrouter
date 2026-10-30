using NexusRouter.Domain.Enums;

namespace NexusRouter.Ingress.Sessions;

/// <summary>
/// Transição recusada pela máquina de estados da sessão FIX.
/// </summary>
public sealed class InvalidSessionStateException : Exception
{
    /// <summary>
    /// Cria a exceção para uma transição cujo predecessor exigido não era o estado observado.
    /// </summary>
    /// <param name="actual">Estado observado no momento da tentativa.</param>
    /// <param name="attempted">Estado de destino da transição recusada.</param>
    /// <param name="expected">Único predecessor aceito para <paramref name="attempted"/>.</param>
    public InvalidSessionStateException(SessionState actual, SessionState attempted, SessionState expected)
        : base($"Transição ilegal de {actual} para {attempted}. O estado exigido era {expected}.")
    {
        Actual = actual;
        Attempted = attempted;
        Expected = expected;
    }

    /// <summary>
    /// Cria a exceção quando o destino admite mais de um predecessor e nenhum deles era o estado observado.
    /// </summary>
    /// <param name="actual">Estado observado no momento da tentativa.</param>
    /// <param name="attempted">Estado de destino da transição recusada.</param>
    public InvalidSessionStateException(SessionState actual, SessionState attempted)
        : base($"Transição ilegal de {actual} para {attempted}.")
    {
        Actual = actual;
        Attempted = attempted;
    }

    /// <summary>
    /// Estado observado quando a transição foi recusada.
    /// </summary>
    public SessionState Actual { get; }

    /// <summary>
    /// Estado que a chamada tentou alcançar.
    /// </summary>
    public SessionState Attempted { get; }

    /// <summary>
    /// Predecessor único exigido, quando a transição tem apenas um estado de origem legal.
    /// </summary>
    public SessionState? Expected { get; }
}
