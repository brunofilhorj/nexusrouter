using NexusRouter.Ingress.Sessions;

namespace NexusRouter.Ingress.Tests;

public sealed class ExponentialBackoffReconnectPolicyTests
{
    [Theory]
    [InlineData(0, 100)]
    [InlineData(1, 200)]
    [InlineData(2, 400)]
    [InlineData(3, 800)]
    public void ComputeDelay_WhenJitterIsDisabled_GrowsExponentially(int attemptNumber, int expectedMilliseconds)
    {
        var policy = new ExponentialBackoffReconnectPolicy(
            baseDelay: TimeSpan.FromMilliseconds(100),
            maxDelay: TimeSpan.FromSeconds(10),
            jitterFactor: 0);

        TimeSpan delay = policy.ComputeDelay(attemptNumber);

        Assert.Equal(TimeSpan.FromMilliseconds(expectedMilliseconds), delay);
    }

    [Theory]
    [InlineData(4)]
    [InlineData(1000)]
    [InlineData(int.MaxValue)]
    public void ComputeDelay_WhenAttemptExceedsCap_ReturnsMaxDelay(int attemptNumber)
    {
        TimeSpan maxDelay = TimeSpan.FromMilliseconds(250);
        var policy = new ExponentialBackoffReconnectPolicy(
            baseDelay: TimeSpan.FromMilliseconds(100),
            maxDelay: maxDelay,
            jitterFactor: 0);

        TimeSpan delay = policy.ComputeDelay(attemptNumber);

        Assert.Equal(maxDelay, delay);
    }

    [Fact]
    public void ComputeDelay_WhenJitterIsEnabled_StaysWithinCappedDelayRange()
    {
        var policy = new ExponentialBackoffReconnectPolicy(
            baseDelay: TimeSpan.FromMilliseconds(100),
            maxDelay: TimeSpan.FromMilliseconds(200),
            jitterFactor: 0.25);
        TimeSpan minimum = TimeSpan.FromMilliseconds(150);
        TimeSpan maximum = TimeSpan.FromMilliseconds(250);

        for (int i = 0; i < 1_000; i++)
        {
            TimeSpan delay = policy.ComputeDelay(attemptNumber: 10);

            Assert.InRange(delay, minimum, maximum);
        }
    }

    [Fact]
    public void ComputeDelay_WhenJitterCouldMakeDelayNegative_ClampsAtZero()
    {
        var policy = new ExponentialBackoffReconnectPolicy(
            baseDelay: TimeSpan.FromMilliseconds(1),
            maxDelay: TimeSpan.FromMilliseconds(1),
            jitterFactor: 2);

        for (int i = 0; i < 1_000; i++)
        {
            Assert.True(policy.ComputeDelay(attemptNumber: 0) >= TimeSpan.Zero);
        }
    }

    [Fact]
    public void ComputeDelay_WhenAttemptIsNegative_ThrowsArgumentOutOfRangeException()
    {
        var policy = new ExponentialBackoffReconnectPolicy(
            baseDelay: TimeSpan.FromMilliseconds(100),
            maxDelay: TimeSpan.FromSeconds(1),
            jitterFactor: 0);

        Assert.Throws<ArgumentOutOfRangeException>(() => policy.ComputeDelay(-1));
    }
}
