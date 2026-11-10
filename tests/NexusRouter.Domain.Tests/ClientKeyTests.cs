using NexusRouter.Domain.Models;

namespace NexusRouter.Domain.Tests;

public sealed class ClientKeyTests
{
    public static TheoryData<string?> InvalidKeys() =>
    [
        null,
        string.Empty,
        "..",
        "A.B",
        "A/B",
        "A\\B",
        "A B",
        "A\u0001B",
        "A\tB",
        "OMS\u00e7",
        "CON",
        "nul",
        "Com1",
        "LPT9",
        new string('A', 65),
    ];

    [Theory]
    [InlineData("OMS1")]
    [InlineData("RISK_1")]
    [InlineData("a-b_C-9")]
    [InlineData("CONSOLE")]
    public void IsValid_SafeCharacters_ReturnsTrue(string value)
    {
        Assert.True(ClientKey.IsValid(value));
    }

    [Fact]
    public void IsValid_SixtyFourCharacters_ReturnsTrue()
    {
        Assert.True(ClientKey.IsValid(new string('A', 64)));
    }

    [Theory]
    [MemberData(nameof(InvalidKeys))]
    public void IsValid_UnsafeValue_ReturnsFalse(string? value)
    {
        Assert.False(ClientKey.IsValid(value));
    }

    [Fact]
    public void TryCreateFix_ValidSender_ReturnsKeyWithSameValue()
    {
        bool created = ClientKey.TryCreateFix("OMS1", out ClientKey key);

        Assert.True(created);
        Assert.Equal("OMS1", key.Value);
    }

    [Fact]
    public void TryCreateFix_TraversalAttempt_ReturnsFalse()
    {
        bool created = ClientKey.TryCreateFix("../evil", out ClientKey key);

        Assert.False(created);
        Assert.Equal(default, key);
    }

    [Fact]
    public void ForWebSocket_Uuid_ReturnsValidThirtySixCharacterKey()
    {
        var id = Guid.Parse("6f1e0b3a-9c2d-4a7e-8b15-2d4c0a9e7f31");

        ClientKey key = ClientKey.ForWebSocket(id);

        Assert.Equal("6f1e0b3a-9c2d-4a7e-8b15-2d4c0a9e7f31", key.Value);
        Assert.True(ClientKey.IsValid(key.Value));
    }

    [Fact]
    public void ForWebSocket_EmptyUuid_Throws()
    {
        Assert.Throws<ArgumentException>(() => ClientKey.ForWebSocket(Guid.Empty));
    }
}
