using SignalAtlas.Analyst;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

/// <summary>
/// M12 (SPEC §8.12): the shared deterministic retrieval/tool + citation layer. Retrieval correct for
/// time / space / protocol queries; template answer cites EVERY record used (P6); empty result →
/// "none found" with ZERO citations and no fabrication. No LLM anywhere here.
/// </summary>
public class AnalystRetrievalTests
{
    private static readonly DateTimeOffset Now = new(2026, 7, 7, 12, 0, 0, TimeSpan.Zero);

    private static AnalystRetrieval Build(
        IEnumerable<Signal>? signals = null,
        IEnumerable<Device>? devices = null,
        IEnumerable<Emitter>? emitters = null,
        IEnumerable<Alert>? alerts = null,
        IEnumerable<SpectrumFrame>? frames = null)
        => new(
            new FakeSignalRepository(signals ?? []),
            new FakeDeviceRepository(devices ?? []),
            new FakeEmitterRepository(emitters ?? []),
            new FakeAlertRepository(alerts ?? []),
            new FakeSpectrumBuffer(frames ?? []),
            new FixedClock(Now));

    [Fact]
    public void WhatChanged_ReturnsRecentAlertsAndSignals_CitingEach()
    {
        var recentAlert = AnalystData.Alert(Alert.NewEmitter, Now.AddMinutes(-10));
        var recentSignal = AnalystData.Signal(1, "LoRa", Now.AddMinutes(-5));
        var oldSignal = AnalystData.Signal(2, "Wi-Fi", Now.AddDays(-3));
        var r = Build(signals: [recentSignal, oldSignal], alerts: [recentAlert])
            .Retrieve(new AnalystIntent(AnalystQueryType.WhatChanged, Window: TimeSpan.FromHours(1)));

        Assert.Equal(2, r.RecordCount); // the recent alert + recent signal, not the 3-day-old one
        Assert.Contains(r.Citations, c => c.Feature == "alert");
        Assert.Contains(r.Citations, c => c.Feature == "signal" && c.Value == "1");
        Assert.DoesNotContain(r.Citations, c => c.Value == "2");
    }

    [Fact]
    public void NearLocation_ReturnsEmittersWithinRadius_ViaHaversine()
    {
        var near = AnalystData.Emitter("e-near", "Wi-Fi", 2_437_000_000, lat: 42.3601, lon: -71.0589);
        var far = AnalystData.Emitter("e-far", "Wi-Fi", 2_437_000_000, lat: 40.0, lon: -71.0);
        var r = Build(emitters: [near, far]).Retrieve(new AnalystIntent(
            AnalystQueryType.NearLocation, Latitude: 42.36, Longitude: -71.059, RadiusMeters: 1000));

        Assert.Equal(1, r.RecordCount);
        Assert.Contains(r.Citations, c => c.Feature == "emitter" && c.Value == "e-near");
        Assert.DoesNotContain(r.Citations, c => c.Value == "e-far");
    }

    [Fact]
    public void ListByProtocol_ReturnsMatchingEmittersAndDevices_CitingEach()
    {
        var wifi = AnalystData.Emitter("e-wifi", "Wi-Fi", 2_437_000_000);
        var lora = AnalystData.Emitter("e-lora", "LoRa", 915_000_000);
        var wifiDev = AnalystData.Device("d-wifi", "Wi-Fi");
        var r = Build(devices: [wifiDev], emitters: [wifi, lora])
            .Retrieve(new AnalystIntent(AnalystQueryType.ListByProtocol, Protocol: "Wi-Fi"));

        Assert.Contains(r.Citations, c => c.Feature == "emitter" && c.Value == "e-wifi");
        Assert.Contains(r.Citations, c => c.Feature == "device" && c.Value == "d-wifi");
        Assert.DoesNotContain(r.Citations, c => c.Value == "e-lora");
    }

    [Fact]
    public void CountByProtocol_CitesEveryEmitterCounted()
    {
        var r = Build(emitters:
        [
            AnalystData.Emitter("e1", "Wi-Fi", 2_437_000_000),
            AnalystData.Emitter("e2", "Wi-Fi", 2_437_000_000),
            AnalystData.Emitter("e3", "LoRa", 915_000_000),
        ]).Retrieve(new AnalystIntent(AnalystQueryType.CountByProtocol));

        Assert.Equal(3, r.Citations.Count);
        Assert.Contains("Wi-Fi", r.Answer);
    }

    [Fact]
    public void UnknownInBand_FiltersUnknownProtocolInBand()
    {
        var unknownIn = AnalystData.Emitter("u-in", "Unknown", 2_437_000_000);
        var unknownOut = AnalystData.Emitter("u-out", "Unknown", 915_000_000);
        var knownIn = AnalystData.Emitter("k-in", "Wi-Fi", 2_437_000_000);
        var r = Build(emitters: [unknownIn, unknownOut, knownIn]).Retrieve(new AnalystIntent(
            AnalystQueryType.UnknownInBand, BandLowHz: 2_400_000_000, BandHighHz: 2_500_000_000));

        Assert.Contains(r.Citations, c => c.Value == "u-in");
        Assert.DoesNotContain(r.Citations, c => c.Value == "u-out");
        Assert.DoesNotContain(r.Citations, c => c.Value == "k-in");
    }

    [Fact]
    public void Occupancy_SummarizesLatestFrame_WithCitation()
    {
        // bins: median ~ -100; two bins well above median+6dB → occupied.
        var frame = AnalystData.Frame(Now, [-100, -100, -40, -101, -38, -100]);
        var r = Build(frames: [frame]).Retrieve(new AnalystIntent(AnalystQueryType.Occupancy));

        Assert.NotEmpty(r.Citations);
        Assert.Contains(r.Citations, c => c.Feature == "spectrum");
    }

    [Fact]
    public void EmptyResult_YieldsNoneFound_WithZeroCitations_NoFabrication()
    {
        var r = Build().Retrieve(new AnalystIntent(AnalystQueryType.ListByProtocol, Protocol: "Wi-Fi"));

        Assert.Empty(r.Citations);
        Assert.Equal(0, r.RecordCount);
        Assert.Contains("None found", r.Answer, StringComparison.OrdinalIgnoreCase);
    }
}
