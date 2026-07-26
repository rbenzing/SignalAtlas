using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SignalAtlas.Tests.Contract;

/// <summary>
/// Contract tests for the Spectrum read endpoints (SPEC §9.2 /spectrum/frames, /spectrum/occupancy,
/// /spectrum/coverage; §8.2 waterfall; §4.4 coverage indicator). Seeded synthetic frames back these
/// so the Spectrum views render offline.
/// </summary>
public class SpectrumEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    // This contract test asserts SEEDED synthetic frames are present — opt in to demo seeding
    // explicitly (SeedDemoData defaults false since the product no longer seeds by default).
    private readonly WebApplicationFactory<Program> _factory =
        factory.WithWebHostBuilder(b => b.UseSetting("SeedDemoData", "true"));

    [Fact]
    public async Task GetFrames_ReturnsEnvelopedFrames_WithBoundedBins()
    {
        var client = _factory.CreateClient();
        using var doc = JsonDocument.Parse(
            await (await client.GetAsync("/api/v1/spectrum/frames")).Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.Equal("v1", root.GetProperty("schemaVersion").GetString());
        var frames = root.GetProperty("payload");
        Assert.True(frames.GetArrayLength() > 0, "seeded frames expected");

        var frame = frames[0];
        Assert.True(frame.TryGetProperty("time", out _));
        Assert.True(frame.GetProperty("centerFreqHz").GetInt64() > 0);
        Assert.True(frame.GetProperty("sampleRateHz").GetInt32() > 0);
        var power = frame.GetProperty("powerDbfs");
        Assert.Equal(JsonValueKind.Array, power.ValueKind);
        Assert.InRange(power.GetArrayLength(), 1, 256); // downsampled to <=256 bins.
    }

    [Fact]
    public async Task GetFrames_CountIsCappedAt256()
    {
        var client = _factory.CreateClient();
        using var doc = JsonDocument.Parse(
            await (await client.GetAsync("/api/v1/spectrum/frames?count=10000")).Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.GetProperty("payload").GetArrayLength() <= 256);
    }

    [Fact]
    public async Task GetOccupancy_ReturnsFreqAndPowerArrays()
    {
        var client = _factory.CreateClient();
        using var doc = JsonDocument.Parse(
            await (await client.GetAsync("/api/v1/spectrum/occupancy")).Content.ReadAsStringAsync());
        var payload = doc.RootElement.GetProperty("payload");

        var freq = payload.GetProperty("freqHz");
        var power = payload.GetProperty("powerDbfs");
        Assert.Equal(JsonValueKind.Array, freq.ValueKind);
        Assert.Equal(freq.GetArrayLength(), power.GetArrayLength());
        Assert.True(freq.GetArrayLength() > 0);
        Assert.InRange(payload.GetProperty("occupiedFraction").GetDouble(), 0.0, 1.0);
    }

    [Fact]
    public async Task GetCoverage_ReturnsAllPriorityBands()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/spectrum/coverage");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var bands = doc.RootElement.GetProperty("payload");
        Assert.Equal(JsonValueKind.Array, bands.ValueKind);

        var keys = bands.EnumerateArray().Select(b => b.GetProperty("key").GetString()).ToList();
        Assert.Contains("adsb-1090", keys);
        Assert.Contains("ism-2400", keys);
        Assert.Contains("ism-sub-ghz", keys);
        Assert.Contains("fm-broadcast", keys);

        foreach (var band in bands.EnumerateArray())
        {
            Assert.False(string.IsNullOrWhiteSpace(band.GetProperty("label").GetString()));
            Assert.True(band.TryGetProperty("covered", out _));
            Assert.True(band.TryGetProperty("lastSeen", out _));
        }
    }

    // Audit #14: GeolocationEngine now has a real runtime consumer via /map/heatmap.
    [Fact]
    public async Task GetHeatmap_ReturnsEnvelopedCells()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/map/heatmap");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode); // gated route, single-operator pass-through
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal("v1", root.GetProperty("schemaVersion").GetString());

        var cells = root.GetProperty("payload");
        Assert.Equal(JsonValueKind.Array, cells.ValueKind); // empty until positioned observations exist
        foreach (var c in cells.EnumerateArray())
        {
            Assert.True(c.TryGetProperty("lat", out _));
            Assert.True(c.TryGetProperty("lon", out _));
            Assert.True(c.GetProperty("count").GetInt32() >= 1);
        }
    }
}
