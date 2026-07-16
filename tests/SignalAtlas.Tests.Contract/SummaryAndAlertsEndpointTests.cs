using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SignalAtlas.Tests.Contract;

public class SummaryAndAlertsEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory = factory;

    // M7 — GET /alerts returns anomaly alerts produced by the real engine (SPEC §8.8, §9.2).
    [Fact]
    public async Task GetAlerts_ReturnsNewEmitterAlert_WithEvidence()
    {
        var client = _factory.CreateClient();
        var resp = await client.GetAsync("/api/v1/alerts");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var payload = doc.RootElement.GetProperty("payload");
        Assert.True(payload.GetArrayLength() > 0);

        var kinds = payload.EnumerateArray().Select(a => a.GetProperty("kind").GetString()).ToList();
        Assert.Contains("new_emitter", kinds);
        foreach (var a in payload.EnumerateArray())
            Assert.True(a.GetProperty("evidence").GetArrayLength() > 0, "alert evidence must be non-empty");
    }

    // M7 — GET /summary answers "how many" (MVP criterion 7 text form, SPEC §9.2).
    [Fact]
    public async Task GetSummary_ReturnsCounts()
    {
        var client = _factory.CreateClient();
        using var doc = JsonDocument.Parse(
            await (await client.GetAsync("/api/v1/summary")).Content.ReadAsStringAsync());
        var p = doc.RootElement.GetProperty("payload");

        Assert.True(p.GetProperty("signalCount").GetInt32() >= 1);
        Assert.True(p.GetProperty("deviceCount").GetInt32() >= 1);
        Assert.True(p.GetProperty("alertCount").GetInt32() >= 1);
    }
}
