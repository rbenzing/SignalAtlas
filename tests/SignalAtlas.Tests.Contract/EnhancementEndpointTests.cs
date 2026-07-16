using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SignalAtlas.Tests.Contract;

/// <summary>
/// M13 (SPEC §8.13, §9.2): the optional-enhancement REST surface — enveloped + gated. Sessions,
/// enhancement-candidate count (no Claude, AC-DA7), start/read runs (offline → queued, AC-DA5),
/// enrichments listing, and accept/reject (AC-DA4). Offline is the default posture (no cloud uplift).
/// </summary>
public class EnhancementEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory = factory;

    private static JsonElement Payload(string body) =>
        JsonDocument.Parse(body).RootElement.GetProperty("payload").Clone();

    [Fact]
    public async Task GetSessions_ReturnsEnvelopedList()
    {
        var resp = await _factory.CreateClient().GetAsync("/api/v1/sessions");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var payload = Payload(await resp.Content.ReadAsStringAsync());
        Assert.Equal(JsonValueKind.Array, payload.ValueKind);
    }

    [Fact]
    public async Task GetEnhancementCandidates_ReturnsCounts_WithoutClaude()
    {
        var resp = await _factory.CreateClient().GetAsync("/api/v1/sessions/sess-1/enhancement-candidates");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        var payload = Payload(await resp.Content.ReadAsStringAsync());
        Assert.True(payload.TryGetProperty("total", out _));
        Assert.True(payload.TryGetProperty("lowConfidenceSignals", out _));
        Assert.True(payload.TryGetProperty("unresolvedDevices", out _));
    }

    [Fact]
    public async Task PostRun_Offline_ReturnsQueuedRun_ThenReadable()
    {
        var client = _factory.CreateClient();
        var start = await client.PostAsJsonAsync("/api/v1/analysis/runs",
            new { sessionId = "sess-1", model = "claude-sonnet-4-6" });

        Assert.Equal(HttpStatusCode.OK, start.StatusCode);
        var run = Payload(await start.Content.ReadAsStringAsync());
        Assert.Equal("queued", run.GetProperty("status").GetString());   // AC-DA5: offline → queued
        Assert.Equal("claude", run.GetProperty("engine").GetString());

        var id = run.GetProperty("id").GetString();
        var read = await client.GetAsync($"/api/v1/analysis/runs/{id}");
        Assert.Equal(HttpStatusCode.OK, read.StatusCode);
        Assert.Equal(id, Payload(await read.Content.ReadAsStringAsync()).GetProperty("id").GetString());
    }

    [Fact]
    public async Task GetRun_UnknownId_Returns404ProblemDetails()
    {
        var resp = await _factory.CreateClient().GetAsync($"/api/v1/analysis/runs/{Guid.NewGuid()}");

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("correlationId", out _));
    }

    [Fact]
    public async Task GetEnrichments_ReturnsEnvelopedList()
    {
        var resp = await _factory.CreateClient().GetAsync("/api/v1/enrichments");

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        Assert.Equal(JsonValueKind.Array, Payload(await resp.Content.ReadAsStringAsync()).ValueKind);
    }

    [Fact]
    public async Task AcceptEnrichment_UnknownId_Returns404()
    {
        var resp = await _factory.CreateClient()
            .PostAsync($"/api/v1/enrichments/{Guid.NewGuid()}/accept", content: null);

        Assert.Equal(HttpStatusCode.NotFound, resp.StatusCode);
    }
}
