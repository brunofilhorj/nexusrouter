using ClientProbe.Kafka;
using ClientProbe.Probe;
using Confluent.Kafka;

namespace ClientProbe.Tests.Kafka;

public sealed class KafkaConsumerPolicyTests
{
    [Theory]
    [InlineData(ProbeState.Connecting, KafkaErrorAction.Fault)]
    [InlineData(ProbeState.Live, KafkaErrorAction.Note)]
    [InlineData(ProbeState.Faulted, KafkaErrorAction.Ignore)]
    [InlineData(ProbeState.Disconnected, KafkaErrorAction.Ignore)]
    public void Classify_AllBrokersDown_DependsOnState(ProbeState state, KafkaErrorAction expected)
    {
        Assert.Equal(expected, KafkaConsumerPolicy.Classify(state, isFatal: false, ErrorCode.Local_AllBrokersDown));
    }

    [Theory]
    [InlineData(ProbeState.Connecting)]
    [InlineData(ProbeState.Live)]
    [InlineData(ProbeState.Faulted)]
    public void Classify_FatalError_AlwaysFaults(ProbeState state)
    {
        Assert.Equal(KafkaErrorAction.Fault, KafkaConsumerPolicy.Classify(state, isFatal: true, ErrorCode.Local_Fatal));
    }

    [Fact]
    public void Classify_OtherNonFatalError_IsIgnored()
    {
        Assert.Equal(KafkaErrorAction.Ignore, KafkaConsumerPolicy.Classify(ProbeState.Live, isFatal: false, ErrorCode.Local_Transport));
    }

    [Fact]
    public void EffectiveGroupId_Latest_UsesASingleUseGroup()
    {
        var (groupId, singleUse) = KafkaConsumerPolicy.EffectiveGroupId("probe-local", "Latest", "ab12cd34");

        Assert.Equal("probe-local-ab12cd34", groupId);
        Assert.True(singleUse);
    }

    [Fact]
    public void EffectiveGroupId_Earliest_KeepsTheConfiguredGroup()
    {
        var (groupId, singleUse) = KafkaConsumerPolicy.EffectiveGroupId("probe-local", "earliest", "ab12cd34");

        Assert.Equal("probe-local", groupId);
        Assert.False(singleUse);
    }
}
