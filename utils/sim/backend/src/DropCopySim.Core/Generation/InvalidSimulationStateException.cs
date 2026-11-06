namespace DropCopySim.Core.Generation;

/// <summary>
/// Transição ou reconfiguração recusada pela máquina de estados de controle da simulação.
/// Traduzida pela API de controle em HTTP 409 (Conflict).
/// </summary>
public sealed class InvalidSimulationStateException : Exception
{
    /// <summary>
    /// Cria a exceção para uma transição recusada a partir do estado observado.
    /// </summary>
    /// <param name="actual">Estado observado no momento da tentativa.</param>
    /// <param name="attempted">Comando de transição recusado.</param>
    public InvalidSimulationStateException(SimulationState actual, string attempted)
        : base($"Transição ilegal de {actual} para o comando '{attempted}'.")
    {
        Actual = actual;
        Attempted = attempted;
    }

    /// <summary>
    /// Cria a exceção com uma mensagem explícita (ex.: reconfiguração que tocou campo travado em Running).
    /// </summary>
    /// <param name="actual">Estado observado no momento da tentativa.</param>
    /// <param name="attempted">Comando de transição ou reconfiguração recusado.</param>
    /// <param name="message">Mensagem descritiva do motivo da recusa.</param>
    public InvalidSimulationStateException(SimulationState actual, string attempted, string message)
        : base(message)
    {
        Actual = actual;
        Attempted = attempted;
    }

    /// <summary>Estado observado quando a operação foi recusada.</summary>
    public SimulationState Actual { get; }

    /// <summary>Comando de transição ou reconfiguração recusado.</summary>
    public string Attempted { get; }
}
