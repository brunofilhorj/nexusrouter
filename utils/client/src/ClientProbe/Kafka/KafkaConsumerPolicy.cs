using Confluent.Kafka;
using ClientProbe.Probe;

namespace ClientProbe.Kafka;

/// <summary>What the adapter does with a librdkafka error.</summary>
public enum KafkaErrorAction
{
    /// <summary>Nothing to report.</summary>
    Ignore,

    /// <summary>Show the text in <see cref="IProbeAdapter.LastError"/> and keep the state; the client reconnects by itself.</summary>
    Note,

    /// <summary>Move to <see cref="ProbeState.Faulted"/>.</summary>
    Fault,
}

/// <summary>Decisions of the Kafka adapter that do not need a broker, kept apart so they can be tested.</summary>
public static class KafkaConsumerPolicy
{
    /// <summary>
    /// Classifies a client error. A fatal error always faults. <c>AllBrokersDown</c> is a transient report
    /// from librdkafka, which reconnects on its own: it faults only while the first connection is being
    /// established, and is a note once the consumer is live.
    /// </summary>
    /// <param name="state">Current adapter state.</param>
    /// <param name="isFatal">Whether the client marked the error fatal.</param>
    /// <param name="code">Client error code.</param>
    /// <returns>The action to take.</returns>
    public static KafkaErrorAction Classify(ProbeState state, bool isFatal, ErrorCode code)
    {
        if (isFatal)
        {
            return KafkaErrorAction.Fault;
        }

        if (code != ErrorCode.Local_AllBrokersDown)
        {
            return KafkaErrorAction.Ignore;
        }

        return state switch
        {
            ProbeState.Connecting => KafkaErrorAction.Fault,
            ProbeState.Live => KafkaErrorAction.Note,
            _ => KafkaErrorAction.Ignore,
        };
    }

    /// <summary>
    /// Group id used for one connect. With <c>Latest</c> the probe wants only new frames, so each connect gets
    /// a fresh group: there is no old member to wait for, no committed offset to resume from, and no
    /// offsets to commit. With <c>Earliest</c> the configured group is kept, so the operator can replay and resume.
    /// </summary>
    /// <param name="groupId">Group id from the profile.</param>
    /// <param name="autoOffsetReset">Profile offset policy: <c>Latest</c> or <c>Earliest</c>.</param>
    /// <param name="suffix">Unique suffix for this connect.</param>
    /// <returns>The group id and whether it is single-use.</returns>
    public static (string GroupId, bool SingleUse) EffectiveGroupId(string groupId, string autoOffsetReset, string suffix)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(groupId);
        ArgumentException.ThrowIfNullOrWhiteSpace(suffix);
        return string.Equals(autoOffsetReset, "Earliest", StringComparison.OrdinalIgnoreCase)
            ? (groupId, false)
            : ($"{groupId}-{suffix}", true);
    }
}
