using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SignalAtlas.Tests.Contract;

/// <summary>
/// Contract tests for the Emitter read endpoints (SPEC §9.2 GET /emitters[/{id}], §8.6). The map
/// consumes the location estimate + uncertainty to plot markers and uncertainty circles.
/// </summary>
public class EmittersEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory = factory;

    [Fact]
    public async Task GetEmitters_ReturnsVersionedEnvelope_WithLocatedEmitter()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/emitters");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;

        Assert.Equal("v1", root.GetProperty("schemaVersion").GetString());
        Assert.NotEqual(Guid.Empty, root.GetProperty("correlationId").GetGuid());
        var payload = root.GetProperty("payload");
        Assert.Equal(JsonValueKind.Array, payload.ValueKind);
        Assert.True(payload.GetArrayLength() > 0, "at least one seeded emitter expected");

        // A seeded emitter carries coordinates + uncertainty + protocol + evidence for the map.
        var located = payload.EnumerateArray().First(e =>
            e.GetProperty("estLatitude").ValueKind == JsonValueKind.Number);
        Assert.InRange(located.GetProperty("estLatitude").GetDouble(), 42.0, 43.0);
        Assert.InRange(located.GetProperty("estLongitude").GetDouble(), -72.0, -71.0);
        Assert.True(located.GetProperty("estUncertaintyM").GetDouble() > 0);
        Assert.False(string.IsNullOrWhiteSpace(located.GetProperty("protocol").GetString()));
        Assert.True(located.GetProperty("evidence").GetArrayLength() > 0);
        Assert.True(located.TryGetProperty("identifiers", out _));
        Assert.True(located.TryGetProperty("confidence", out _));
    }

    [Fact]
    public async Task GetEmitterById_ReturnsSingleEmitterEnvelope()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/emitters/emitter-wifi-ap-lobby");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var payload = doc.RootElement.GetProperty("payload");
        Assert.Equal("emitter-wifi-ap-lobby", payload.GetProperty("id").GetString());
        Assert.Equal("Wi-Fi", payload.GetProperty("protocol").GetString());
    }

    [Fact]
    public async Task GetEmitterById_Missing_Returns404ProblemDetails()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/emitters/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        Assert.Equal("application/problem+json", resp.Content.Headers.ContentType?.MediaType);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        Assert.Equal(404, root.GetProperty("status").GetInt32());
        Assert.True(root.TryGetProperty("correlationId", out _));
    }
}
