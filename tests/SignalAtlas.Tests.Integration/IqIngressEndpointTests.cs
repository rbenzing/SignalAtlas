using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Integration;

public class IqIngressEndpointTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    private sealed class CapturingNotifier : ILiveNotifier
    {
        public readonly TaskCompletionSource<SpectrumFrame> First =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void SpectrumFrame(SpectrumFrame frame) => First.TrySetResult(frame);
        public void SignalCreated(Signal signal) { }
        public void EmitterUpdated(Emitter emitter) { }
        public void AlertRaised(Alert alert) { }
        public void DeviceDetermined(Device device) { }
    }

    [Fact]
    public async Task Streaming_ConfigThenIqBlock_BroadcastsSpectrumFrame()
    {
        var notifier = new CapturingNotifier();
        var app = factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll(typeof(ILiveNotifier));
                s.AddSingleton<ILiveNotifier>(notifier);
            }));

        var wsClient = app.Server.CreateWebSocketClient();
        var uri = new UriBuilder(app.Server.BaseAddress) { Scheme = "ws", Path = "/ingest/iq" }.Uri;
        using var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);

        var config = JsonSerializer.Serialize(new
        {
            type = "config",
            centerFreqHz = 915_000_000L,
            sampleRateHz = 2_000_000,
            samplesPerBlock = 8,
            collectorId = "web-hackrf-test",
        });
        await ws.SendAsync(Encoding.UTF8.GetBytes(config), WebSocketMessageType.Text, true, CancellationToken.None);

        // One block of 8 samples = 16 interleaved int8 bytes.
        var iq = new byte[16];
        for (int i = 0; i < iq.Length; i++) iq[i] = (byte)(i % 2 == 0 ? 100 : 50);
        await ws.SendAsync(iq, WebSocketMessageType.Binary, true, CancellationToken.None);

        var completed = await Task.WhenAny(notifier.First.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(notifier.First.Task, completed);
        var frame = await notifier.First.Task;
        Assert.Equal(915_000_000, frame.CenterFreqHz);
        Assert.Equal(2_000_000, frame.SampleRateHz);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Streaming_ConfigThenDelayThenIq_SurvivesEmptyChannelWait()
    {
        // Regression test (user-reported): after the config frame, the per-connection pipeline
        // starts draining BrowserUploadSampleSource.Blocks() while the channel is still empty —
        // exactly the real streaming timeline, where IQ arrives a beat after connect. The wait on
        // the empty channel returns a not-yet-completed ValueTask; blocking on it incorrectly
        // (without .AsTask()) threw "The asynchronous operation has not completed." and faulted the
        // pipeline, so no frames ever flowed. Here we deliberately pause between config and the
        // first IQ block and assert a spectrumFrame still arrives — i.e. the empty-channel wait
        // parked instead of throwing.
        var notifier = new CapturingNotifier();
        var app = factory.WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll(typeof(ILiveNotifier));
                s.AddSingleton<ILiveNotifier>(notifier);
            }));

        var wsClient = app.Server.CreateWebSocketClient();
        var uri = new UriBuilder(app.Server.BaseAddress) { Scheme = "ws", Path = "/ingest/iq" }.Uri;
        using var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);

        var config = JsonSerializer.Serialize(new
        {
            type = "config",
            centerFreqHz = 433_920_000L,
            sampleRateHz = 2_000_000,
            samplesPerBlock = 8,
            collectorId = "web-hackrf-test",
        });
        await ws.SendAsync(Encoding.UTF8.GetBytes(config), WebSocketMessageType.Text, true, CancellationToken.None);

        // Pause so the pipeline is parked in the empty-channel wait before any IQ arrives.
        await Task.Delay(300);
        Assert.False(notifier.First.Task.IsFaulted, "pipeline faulted while waiting on the empty channel");

        var iq = new byte[16];
        for (int i = 0; i < iq.Length; i++) iq[i] = (byte)(i % 2 == 0 ? 100 : 50);
        await ws.SendAsync(iq, WebSocketMessageType.Binary, true, CancellationToken.None);

        var completed = await Task.WhenAny(notifier.First.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(notifier.First.Task, completed);
        var frame = await notifier.First.Task;
        Assert.Equal(433_920_000, frame.CenterFreqHz);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    [Fact]
    public async Task Streaming_ClearsDemoSeed_DevicesAndEmittersBecomeEmpty()
    {
        // When a real device starts streaming, the seeded demo data must be cleared so the UI shows
        // live data only. Devices/emitters have no live data yet (decode/geolocation deferred), so
        // they must go EMPTY once a stream starts.
        var app = factory.WithWebHostBuilder(b => { });
        var client = app.CreateClient();

        // Seed is present before any device connects.
        Assert.True((await GetPayloadCount(client, "/api/v1/emitters")) > 0);
        Assert.True((await GetPayloadCount(client, "/api/v1/devices")) > 0);

        var wsClient = app.Server.CreateWebSocketClient();
        var uri = new UriBuilder(app.Server.BaseAddress) { Scheme = "ws", Path = "/ingest/iq" }.Uri;
        using var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);
        var config = JsonSerializer.Serialize(new
        {
            type = "config",
            centerFreqHz = 915_000_000L,
            sampleRateHz = 2_000_000,
            samplesPerBlock = 8,
            collectorId = "web-hackrf-test",
        });
        await ws.SendAsync(Encoding.UTF8.GetBytes(config), WebSocketMessageType.Text, true, CancellationToken.None);
        await Task.Delay(200); // let the handler run OnDeviceStreamStarted()

        Assert.Equal(0, await GetPayloadCount(client, "/api/v1/emitters"));
        Assert.Equal(0, await GetPayloadCount(client, "/api/v1/devices"));

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    private static async Task<int> GetPayloadCount(System.Net.Http.HttpClient client, string path)
    {
        using var doc = JsonDocument.Parse(await (await client.GetAsync(path)).Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("payload").GetArrayLength();
    }

    [Fact]
    public async Task Streaming_ImmediateClientClose_CompletesHandshakeGracefully()
    {
        // Regression test: previously the server's first ReceiveAsync (before any config frame)
        // sat outside the try/finally, so an immediate client-initiated Close made the handler
        // `return;` without ever sending a Close frame back. The client's own CloseAsync would
        // then hang or throw (ObjectDisposedException/IOException) once `using var socket`
        // disposed the connection. It must now complete cleanly because the whole handler body,
        // including the first receive, is covered by the single finally that closes gracefully.
        var app = factory.WithWebHostBuilder(b => { });
        var wsClient = app.Server.CreateWebSocketClient();
        var uri = new UriBuilder(app.Server.BaseAddress) { Scheme = "ws", Path = "/ingest/iq" }.Uri;
        using var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);

        // Disconnect immediately — no config frame was ever sent.
        var closeTask = ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None);
        var completed = await Task.WhenAny(closeTask, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(closeTask, completed);
        await closeTask; // rethrows if CloseAsync faulted — must not throw.
        Assert.Equal(WebSocketState.Closed, ws.State);
    }

    [Fact]
    public async Task Streaming_InvalidFirstFrame_ServerClosesPromptly()
    {
        // Regression test: a non-text (or unparseable-config) first frame hits the early-exit
        // `return;` before any pipeline starts, landing in the finally's Open-state branch of
        // CloseGracefullyAsync. That branch used to call the blocking socket.CloseAsync, which
        // waits for the peer's Close echo. A client that sends garbage and then goes idle
        // (never replying with its own Close) would pin the request open indefinitely, since
        // ctx.RequestAborted only fires on an actual disconnect. The fix switched that branch to
        // the send-only CloseOutputAsync, so the server must push its Close frame promptly
        // without waiting on us — proven here by never sending a Close from the client and still
        // observing one arrive quickly.
        var app = factory.WithWebHostBuilder(b => { });
        var wsClient = app.Server.CreateWebSocketClient();
        var uri = new UriBuilder(app.Server.BaseAddress) { Scheme = "ws", Path = "/ingest/iq" }.Uri;
        using var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);

        // First frame is binary, not the expected text config frame — triggers the early-exit
        // path. The client deliberately never sends a Close frame of its own.
        var garbage = new byte[] { 1, 2, 3, 4 };
        await ws.SendAsync(garbage, WebSocketMessageType.Binary, true, CancellationToken.None);

        var buffer = new byte[1024];
        var receiveTask = ws.ReceiveAsync(buffer, CancellationToken.None);
        var completed = await Task.WhenAny(receiveTask, Task.Delay(TimeSpan.FromSeconds(5)));

        Assert.Same(receiveTask, completed);
        var result = await receiveTask;
        Assert.Equal(WebSocketMessageType.Close, result.MessageType);
    }
}
