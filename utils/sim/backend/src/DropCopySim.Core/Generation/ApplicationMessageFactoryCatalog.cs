namespace DropCopySim.Core.Generation;

/// <summary>
/// Builds the application-message factories enabled by <see cref="SimulationConfig.EnabledMessageTypes"/>.
/// </summary>
public static class ApplicationMessageFactoryCatalog
{
    /// <summary>
    /// Creates one factory per enabled MsgType, in config order.
    /// The default config (<c>["8"]</c>) returns only <see cref="ExecutionReportFactory"/>.
    /// </summary>
    /// <param name="senderCompId">SenderCompID for the simulated B3 side.</param>
    /// <param name="targetCompId">TargetCompID for the connected client.</param>
    /// <param name="config">Simulation options whose <c>EnabledMessageTypes</c> selects the factories.</param>
    /// <returns>The enabled factories. OrderCancelReject and BusinessMessageReject are absent unless configured.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="config"/> is null.</exception>
    /// <exception cref="ArgumentException">
    /// A CompID is invalid, or <see cref="SimulationConfig.EnabledMessageTypes"/> contains an unsupported MsgType.
    /// </exception>
    public static IReadOnlyList<IFixMessageFactory> Create(
        string senderCompId,
        string targetCompId,
        SimulationConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);

        var factories = new List<IFixMessageFactory>(config.EnabledMessageTypes.Count);
        foreach (string msgType in config.EnabledMessageTypes)
        {
            bool testMessageIndicator = config.TestMessageIndicator;
            IFixMessageFactory factory = msgType switch
            {
                ExecutionReportFactory.MsgType => new ExecutionReportFactory(
                    senderCompId, targetCompId, testMessageIndicator),
                OrderCancelRejectFactory.MsgType => new OrderCancelRejectFactory(
                    senderCompId, targetCompId, testMessageIndicator),
                BusinessMessageRejectFactory.MsgType => new BusinessMessageRejectFactory(
                    senderCompId, targetCompId, testMessageIndicator),
                _ => throw new ArgumentException(
                    $"EnabledMessageTypes value '{msgType}' is not supported. Supported values are 8, 9, and j.",
                    nameof(config)),
            };
            factories.Add(factory);
        }

        return factories;
    }
}
