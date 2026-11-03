using System.Diagnostics;

namespace NexusRouter.Router;

/// <summary>
/// Fonte centralizada de atividades de tracing distribuído para o hot path do NexusRouter.
/// </summary>
/// <remarks>
/// O nome <c>"NexusRouter.HotPath"</c> é usado para registrar o <see cref="ActivitySource"/>
/// no OpenTelemetry SDK. Quando não há listener/sampler ativo, <see cref="TryStartRouteActivity"/>
/// retorna <see langword="null"/> sem qualquer alocação, garantindo zero overhead quando o
/// tracing está desabilitado (NFR-OBS-04: padrão 0% de sampling).
/// </remarks>
public static class NexusRouterTracing
{
    /// <summary>Nome do <see cref="ActivitySource"/> publicado ao SDK de tracing.</summary>
    public const string SourceName = "NexusRouter.HotPath";

    /// <summary>Versão do source, exposta no atributo <c>otel.library.version</c>.</summary>
    public const string SourceVersion = "1.0.0";

    /// <summary>
    /// <see cref="ActivitySource"/> estático compartilhado por todo o processo.
    /// </summary>
    /// <remarks>
    /// A instância é <see langword="static"/> e criada uma única vez. <see cref="ActivitySource"/>
    /// é thread-safe e leve quando não há listener registrado.
    /// </remarks>
    public static readonly ActivitySource Source = new ActivitySource(SourceName, SourceVersion);

    /// <summary>
    /// Inicia uma <see cref="Activity"/> para o span de roteamento somente quando
    /// há um listener/sampler ativo, evitando overhead quando o tracing está desligado.
    /// </summary>
    /// <param name="sessionId">Identificador da sessão — adicionado como tag.</param>
    /// <returns>
    /// A <see cref="Activity"/> iniciada, ou <see langword="null"/> se não houver listener.
    /// O chamador deve fazer <c>using var activity = TryStartRouteActivity(...)</c>
    /// para garantir o fechamento automático do span.
    /// </returns>
    /// <remarks>Zero allocation quando não há listener. Safe to call on the hot path.</remarks>
    public static Activity? TryStartRouteActivity(string sessionId)
    {
        // HasListeners() é uma verificação O(1) que não aloca.
        // Se nenhum listener estiver registrado, StartActivity também retorna null,
        // mas verificar HasListeners() explicitamente documenta a intenção e é ligeiramente mais rápido.
        if (!Source.HasListeners())
        {
            return null;
        }

        Activity? activity = Source.StartActivity(
            name: "NexusRouter.Route",
            kind: ActivityKind.Internal);

        activity?.SetTag("session_id", sessionId);
        return activity;
    }

    /// <summary>
    /// Libera o <see cref="ActivitySource"/> estático ao encerrar o processo.
    /// </summary>
    public static void Dispose() => Source.Dispose();
}
