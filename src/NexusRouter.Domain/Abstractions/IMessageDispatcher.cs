using NexusRouter.Domain.Models;

namespace NexusRouter.Domain.Abstractions;

/// <summary>
/// Publica mensagens já roteadas em um destino downstream.
/// </summary>
/// <remarks>
/// O hot path só chama <see cref="TryEnqueue"/>. O I/O do broker acontece no loop de consumo do próprio dispatcher.
/// </remarks>
public interface IMessageDispatcher
{
    /// <summary>
    /// Nome único do dispatcher, usado na configuração de rotas e nos registros de auditoria.
    /// </summary>
    string DispatcherId { get; }

    /// <summary>
    /// Indica se o dispatcher aceita novas mensagens. A leitura não exige lock.
    /// </summary>
    bool IsHealthy { get; }

    /// <summary>
    /// Inicia o loop de consumo e os recursos de publicação.
    /// </summary>
    /// <param name="cancellationToken">Token que cancela a inicialização.</param>
    /// <returns>Tarefa concluída quando o dispatcher está pronto para receber <see cref="TryEnqueue"/>.</returns>
    ValueTask StartAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Encerra o loop de consumo e libera os recursos de publicação.
    /// </summary>
    /// <param name="cancellationToken">Token que cancela a espera pelo encerramento.</param>
    /// <returns>Tarefa concluída quando o dispatcher parou de publicar.</returns>
    ValueTask StopAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Tenta enfileirar um payload sem bloquear a thread chamadora.
    /// </summary>
    /// <param name="payload">Mensagem já preparada pelo router. O dispatcher passa a ser responsável pelo buffer quando o retorno é <see langword="true"/>.</param>
    /// <returns><see langword="true"/> quando o payload foi aceito; <see langword="false"/> quando o channel está cheio ou o dispatcher recusou a mensagem.</returns>
    /// <remarks>
    /// Não bloqueia e não aloca. Seguro para chamada no hot path.
    /// Channel cheio ou dispatcher indisponível retorna <see langword="false"/>; essa recusa não é comunicada por exceção.
    /// Quando o retorno é <see langword="false"/>, o chamador continua dono do payload e do buffer.
    /// </remarks>
    bool TryEnqueue(DispatchPayload payload);

    /// <summary>
    /// Ocorre quando a publicação downstream falha no loop de consumo.
    /// O primeiro argumento é <see cref="DispatcherId"/>; o segundo é a exceção da falha.
    /// </summary>
    /// <remarks>
    /// O evento é disparado fora do hot path, na task de consumo do dispatcher.
    /// </remarks>
    event Action<string, Exception>? OnDispatchError;
}
