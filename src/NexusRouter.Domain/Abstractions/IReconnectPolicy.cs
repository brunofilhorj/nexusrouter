namespace NexusRouter.Domain.Abstractions;

/// <summary>
/// Calcula a espera antes da próxima tentativa de conexão de uma sessão.
/// </summary>
public interface IReconnectPolicy
{
    /// <summary>
    /// Calcula o atraso da tentativa informada.
    /// </summary>
    /// <param name="attemptNumber">Quantidade de falhas já observadas nesta sequência de reconexão. Zero é a primeira espera.</param>
    /// <returns>Atraso antes da próxima tentativa. Implementações devem retornar um valor não negativo.</returns>
    /// <remarks>
    /// Cálculo puro: sem I/O, sem espera e sem efeito sobre o relógio. Quem chama é que aplica o atraso.
    /// </remarks>
    TimeSpan ComputeDelay(int attemptNumber);
}
