using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;

namespace SignalAtlas.Tests.Contract;

/// <summary>
/// M12 (SPEC §8.12, §9.2): POST /api/v1/analyst/query returns an enveloped, grounded, cited answer
/// with a mode; empty text → 400 RFC 7807 problem-details. Offline by default (no cloud uplift).
/// </summary>
public class AnalystEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly WebApplicationFactory<Program> _factory = factory;

    [Fact]
    public async Task PostQuery_ReturnsEnvelopedAnswer_WithCitationsAndMode()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/analyst/query", new { text = "how many emitters are there" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var payload = doc.RootElement.GetProperty("payload");

        Assert.False(string.IsNullOrWhiteSpace(payload.GetProperty("text").GetString()));
        Assert.Equal("offline", payload.GetProperty("mode").GetString());
        Assert.Equal("CountByProtocol", payload.GetProperty("queryType").GetString());
        Assert.True(payload.GetProperty("citations").GetArrayLength() > 0, "answer must cite the records used (P6)");
    }

    [Fact]
    public async Task PostQuery_UnsupportedPhrasing_ReturnsCapabilityFallback_NoCitations()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/analyst/query", new { text = "sing me a song" });

        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var payload = doc.RootElement.GetProperty("payload");

        Assert.Equal("Unsupported", payload.GetProperty("queryType").GetString());
        Assert.Equal(0, payload.GetProperty("citations").GetArrayLength());
    }

    [Fact]
    public async Task PostQuery_EmptyText_Returns400ProblemDetails()
    {
        var client = _factory.CreateClient();
        var resp = await client.PostAsJsonAsync("/api/v1/analyst/query", new { text = "" });

        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        Assert.True(doc.RootElement.TryGetProperty("correlationId", out _), "problem-details carries correlationId");
    }
}
