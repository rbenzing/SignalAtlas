using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using SignalAtlas.Api;
using SignalAtlas.Domain;
using Xunit;

namespace SignalAtlas.Tests.Integration;

// RF Audio Player /audio WebSocket egress (design §3/§6).
public class AudioEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private const int Fs = 25_000; // == AudioDemodulator.TargetAudioRateHz: exact-decimation passthrough.

    private static IqBlock Block()
    {
        int n = 64;
        var i = new float[n];
        var q = new float[n];
        double phase = 0.0;
        for (int k = 0; k < n; k++)
        {
            i[k] = (float)Math.Cos(phase);
            q[k] = (float)Math.Sin(phase);
            phase += 2 * Math.PI * 1000.0 / Fs;
        }
        return new IqBlock(915_000_000, Fs, i, q);
    }

    [Fact]
    public async Task ConnectSendConfig_FeedIqViaHub_BinaryPcmFramesArrive()
    {
        var app = factory.WithWebHostBuilder(b => { });

        var wsClient = app.Server.CreateWebSocketClient();
        var uri = new UriBuilder(app.Server.BaseAddress) { Scheme = "ws", Path = "/audio" }.Uri;
        using var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);

        var config = JsonSerializer.Serialize(new { mode = "am", enabled = true });
        await ws.SendAsync(Encoding.UTF8.GetBytes(config), WebSocketMessageType.Text, true, CancellationToken.None);

        // Same singleton the endpoint's AudioHub.Register() subscriber will fan into -- the design's
        // "or directly via the hub singleton resolved from app.Services" test path.
        var hub = app.Services.GetRequiredService<AudioHub>();

        var buffer = new byte[1 << 16];
        var receiveTask = ws.ReceiveAsync(buffer, CancellationToken.None);

        // The server-side handler registers its subscriber asynchronously after the WS handshake
        // completes; keep offering blocks until either a frame arrives or we give up.
        for (int attempt = 0; attempt < 100 && !receiveTask.IsCompleted; attempt++)
        {
            hub.Accept(Block());
            await Task.WhenAny(receiveTask, Task.Delay(50));
        }

        var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(receiveTask, completed);
        var result = await receiveTask;

        Assert.Equal(WebSocketMessageType.Binary, result.MessageType);
        Assert.True(result.Count > 0, "expected a non-empty PCM16LE frame");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Disabled_NoConfig_NoFramesArrive()
    {
        // Guards the "zero cost when nobody is listening / not enabled" no-op: connecting alone
        // (never sending an enabling config frame) must never produce audio, even while blocks flow.
        var app = factory.WithWebHostBuilder(b => { });
        var wsClient = app.Server.CreateWebSocketClient();
        var uri = new UriBuilder(app.Server.BaseAddress) { Scheme = "ws", Path = "/audio" }.Uri;
        using var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);

        var hub = app.Services.GetRequiredService<AudioHub>();
        for (int n = 0; n < 10; n++)
            hub.Accept(Block());

        var buffer = new byte[1 << 16];
        using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(300));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await ws.ReceiveAsync(buffer, cts.Token));

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public void AudioRoute_IsNotBehindTheAuthorizationGate()
    {
        // /audio is ungated, loopback posture (design §3/§5), like /ingest/iq and /hub/live -- it
        // must never be swept up by the /api/v1 auth-gate contract test (RouteGatedTests), which
        // only enumerates routes starting with "/api/v1". This test independently pins that /audio
        // exists as a route and explicitly carries NO AuthGateMarker.
        _ = factory.CreateClient();
        var sources = factory.Services.GetRequiredService<EndpointDataSource>();

        var audioEndpoint = sources.Endpoints
            .OfType<RouteEndpoint>()
            .SingleOrDefault(e => e.RoutePattern.RawText == "/audio");

        Assert.NotNull(audioEndpoint);
        Assert.False(audioEndpoint!.RoutePattern.RawText?.StartsWith("/api/v1", StringComparison.Ordinal));
        Assert.Null(audioEndpoint.Metadata.GetMetadata<AuthGateMarker>());
    }
}
