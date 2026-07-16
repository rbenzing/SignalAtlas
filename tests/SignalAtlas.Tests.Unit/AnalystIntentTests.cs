using SignalAtlas.Analyst;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M12 (SPEC §8.12): the deterministic intent classifier maps each known query type → correct type
/// + slots, with NO LLM. Slot extraction: protocol names, time phrases, coords/radius, bands.
/// </summary>
public class AnalystIntentTests
{
    private static AnalystIntent Classify(string text) => new IntentClassifier().Classify(text);

    [Theory]
    [InlineData("what changed today")]
    [InlineData("what's new")]
    [InlineData("any new activity in the last hour")]
    public void MapsWhatChanged(string q) =>
        Assert.Equal(AnalystQueryType.WhatChanged, Classify(q).QueryType);

    [Fact]
    public void WhatChanged_LastHour_ExtractsOneHourWindow()
    {
        var intent = Classify("what changed in the last hour");
        Assert.Equal(AnalystQueryType.WhatChanged, intent.QueryType);
        Assert.Equal(TimeSpan.FromHours(1), intent.Window);
    }

    [Fact]
    public void WhatChanged_LastNMinutes_ExtractsWindow()
    {
        var intent = Classify("what's new in the last 15 minutes");
        Assert.Equal(TimeSpan.FromMinutes(15), intent.Window);
    }

    [Fact]
    public void NearLocation_ExtractsCoordsAndRadius()
    {
        var intent = Classify("emitters near 42.36,-71.06 within 500 m");
        Assert.Equal(AnalystQueryType.NearLocation, intent.QueryType);
        Assert.Equal(42.36, intent.Latitude!.Value, 3);
        Assert.Equal(-71.06, intent.Longitude!.Value, 3);
        Assert.Equal(500.0, intent.RadiusMeters!.Value, 3);
    }

    [Fact]
    public void UnknownInBand_ExtractsBand()
    {
        var intent = Classify("show unknown emitters in 2.4GHz");
        Assert.Equal(AnalystQueryType.UnknownInBand, intent.QueryType);
        Assert.NotNull(intent.BandLowHz);
        Assert.True(intent.BandLowHz <= 2_400_000_000 && intent.BandHighHz >= 2_480_000_000);
    }

    [Theory]
    [InlineData("how many devices are there")]
    [InlineData("count the emitters")]
    public void MapsCountByProtocol(string q) =>
        Assert.Equal(AnalystQueryType.CountByProtocol, Classify(q).QueryType);

    [Fact]
    public void ListByProtocol_ExtractsProtocol()
    {
        var intent = Classify("show wifi devices");
        Assert.Equal(AnalystQueryType.ListByProtocol, intent.QueryType);
        Assert.Equal("Wi-Fi", intent.Protocol);
    }

    [Theory]
    [InlineData("list lora", "LoRa")]
    [InlineData("show me the adsb emitters", "ADS-B")]
    [InlineData("list ble", "BLE")]
    public void ListByProtocol_NormalizesProtocolNames(string q, string expected) =>
        Assert.Equal(expected, Classify(q).Protocol);

    [Theory]
    [InlineData("what's the busiest band")]
    [InlineData("show spectrum occupancy")]
    public void MapsOccupancy(string q) =>
        Assert.Equal(AnalystQueryType.Occupancy, Classify(q).QueryType);

    [Fact]
    public void UnrecognizedPhrasing_IsUnsupported() =>
        Assert.Equal(AnalystQueryType.Unsupported, Classify("tell me a joke about penguins").QueryType);
}
