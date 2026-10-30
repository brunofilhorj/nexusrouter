using NexusRouter.Domain.Models;

namespace NexusRouter.Domain.Abstractions;

/// <summary>
/// Recebe frames de aplicação já enquadrados, na thread da sessão.
/// </summary>
/// <remarks>
/// <see cref="Route"/> é síncrono. <see cref="IngressFrame"/> só é válido durante a chamada:
/// a implementação copia o que precisa antes de retornar.
/// </remarks>
public interface IMessageRouter
{
    /// <summary>
    /// Encaminha um frame de aplicação.
    /// </summary>
    /// <param name="frame">Frame apoiado no buffer do pipe. Não atravessa a chamada.</param>
    /// <remarks>
    /// Sem <c>await</c> e sem <c>lock</c>. O hot path da sessão chama este método e segue para o próximo frame.
    /// </remarks>
    void Route(in IngressFrame frame);
}
