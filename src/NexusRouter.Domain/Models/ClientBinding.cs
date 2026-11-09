using NexusRouter.Domain.Abstractions;

namespace NexusRouter.Domain.Models;

/// <summary>
/// Index and durable state of one known client.
/// </summary>
/// <param name="Index">Sequence numbers and <c>.cidx</c> slots.</param>
/// <param name="State">Values the flusher writes to <c>state.json</c>.</param>
public readonly record struct ClientBinding(IClientIndex Index, IConsumerState State);
