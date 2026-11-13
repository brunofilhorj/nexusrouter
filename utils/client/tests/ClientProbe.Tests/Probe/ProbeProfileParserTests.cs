using System.Text.Json;
using ClientProbe.Probe;
using Microsoft.Extensions.Options;

namespace ClientProbe.Tests.Probe;

public sealed class ProbeProfileParserTests
{
    private static ProbeProfileParser Parser(string replayMode = "") =>
        new(Options.Create(new ClientProbeOptions { Fix = { ReplayMode = replayMode } }));

    private static FixProfile Parse(string json, string replayMode = "")
    {
        using var doc = JsonDocument.Parse(json);
        return Assert.IsType<FixProfile>(Parser(replayMode).Parse(ProbeTransport.Fix, doc.RootElement));
    }

    [Fact]
    public void Parse_FixWithoutReplayMode_OmitsTag()
    {
        var profile = Parse("""{"host":"127.0.0.1","port":6001}""");
        Assert.Null(profile.ReplayMode);
    }

    [Fact]
    public void Parse_FixEmptyReplayMode_OmitsTag()
    {
        var profile = Parse("""{"replayMode":""}""", replayMode: "StartOfDay");
        Assert.Null(profile.ReplayMode);
    }

    [Fact]
    public void Parse_FixReplayModeNames_MapsWireValues()
    {
        Assert.Equal(ProbeReplayMode.FromLogon, Parse("""{"replayMode":"FromLogon"}""").ReplayMode);
        Assert.Equal(ProbeReplayMode.StartOfDay, Parse("""{"replayMode":"2"}""").ReplayMode);
    }

    [Fact]
    public void Parse_FixMissingReplayMode_UsesConfiguredDefault()
    {
        var profile = Parse("""{"host":"127.0.0.1"}""", replayMode: "StartOfDay");
        Assert.Equal(ProbeReplayMode.StartOfDay, profile.ReplayMode);
    }

    [Fact]
    public void Parse_FixUnknownReplayMode_Throws()
    {
        var ex = Assert.Throws<ProbeValidationException>(() => Parse("""{"replayMode":"yesterday"}"""));
        Assert.Equal("invalid_profile", ex.Code);
    }
}
