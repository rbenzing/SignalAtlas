using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SignalAtlas.Tests.Contract;

public class DevicesEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory = factory;

    // M3 — GET /devices returns the versioned envelope and a device determined via the real
    // decode→resolve path (SPEC §8.4, §9.2).
    [Fact]
    public async Task GetDevices_ReturnsDeterminedDevice_WithEvidence()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/devices");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.Equal("v1", root.GetProperty("schemaVersion").GetString());
        var payload = root.GetProperty("payload");
        Assert.Equal(JsonValueKind.Array, payload.ValueKind);

        var device = payload[0];
        Assert.Equal("Aircraft", device.GetProperty("deviceType").GetString());
        Assert.Equal("ADS-B", device.GetProperty("protocol").GetString());
        Assert.True(device.GetProperty("evidence").GetArrayLength() > 0, "device evidence must be non-empty");
    }
}
