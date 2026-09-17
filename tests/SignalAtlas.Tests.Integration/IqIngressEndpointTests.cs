using System.Linq;
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
        // they must go EMPTY once a stream starts. Opt in to demo seeding explicitly (SeedDemoData
        // defaults false since the product no longer seeds by default) so there is something to clear.
        var app = factory.WithWebHostBuilder(b => b.UseSetting("SeedDemoData", "true"));
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

        // The handler runs OnDeviceStreamStarted() on its own receive loop — SendAsync above gives no
        // happens-before edge, so poll for the clear rather than sleeping a fixed 200 ms and hoping.
        await WaitForCountAsync(client, "/api/v1/emitters", c => c == 0,
            "a live stream must clear the demo-seeded emitters");
        await WaitForCountAsync(client, "/api/v1/devices", c => c == 0,
            "a live stream must clear the demo-seeded devices");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }

    private static async Task<int> GetPayloadCount(System.Net.Http.HttpClient client, string path)
    {
        using var doc = JsonDocument.Parse(await (await client.GetAsync(path)).Content.ReadAsStringAsync());
        return doc.RootElement.GetProperty("payload").GetArrayLength();
    }

    /// <summary>How long a condition-based wait will keep polling before failing the test.</summary>
    private static readonly TimeSpan ConditionTimeout = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>
    /// Polls <paramref name="path"/> until its payload count satisfies <paramref name="predicate"/>.
    /// <para>
    /// WHY THIS EXISTS: <c>ws.SendAsync(...)</c> only completes the CLIENT-side write. The server
    /// consumes config frames on its own <c>ReceiveAsync</c> loop in
    /// <see cref="SignalAtlas.Api"/>'s <c>/ingest/iq</c> handler, so there is NO happens-before edge
    /// between sending a frame and the server acting on it. Sleeping a fixed <c>Task.Delay(150)</c>
    /// and asserting was a race: it failed ~1 run in 12 under CI-like parallelism (4 cores), which
    /// is what broke the pipeline. Poll for the effect with a generous ceiling instead — that is
    /// fast when the server is quick and still correct when a loaded runner makes it slow.
    /// </para>
    /// </summary>
    private static async Task WaitForCountAsync(
        System.Net.Http.HttpClient client, string path, Func<int, bool> predicate, string because)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var count = await GetPayloadCount(client, path);
        while (!predicate(count))
        {
            if (sw.Elapsed > ConditionTimeout)
                Assert.Fail(
                    $"Timed out after {ConditionTimeout.TotalSeconds:0.#}s waiting on {path}: {because}. Last count: {count}.");
            await Task.Delay(PollInterval);
            count = await GetPayloadCount(client, path);
        }
    }

    /// <summary>
    /// Asserts the payload count at <paramref name="path"/> stays &gt; 0 for a short settling window.
    /// Used for "this must NOT be cleared" checks, where polling for a change cannot help. It cannot
    /// prove the server already consumed the frame (no ack exists), so it is a regression guard
    /// against an over-eager clear rather than a strict ordering proof — its failure mode is a false
    /// PASS under extreme scheduling delay, never a false failure.
    /// </summary>
    private static async Task AssertStaysPopulatedAsync(
        System.Net.Http.HttpClient client, string path, string because)
    {
        for (var i = 0; i < 10; i++)
        {
            Assert.True(await GetPayloadCount(client, path) > 0, because);
            await Task.Delay(PollInterval);
        }
    }

    private static string ConfigFrame(long centerFreqHz, int? vgaDb = null) => JsonSerializer.Serialize(new
    {
        type = "config",
        centerFreqHz,
        sampleRateHz = 2_000_000,
        samplesPerBlock = 8,
        collectorId = "web-hackrf-test",
        vgaDb,
    });

    [Fact]
    public async Task Streaming_RetuneToDifferentCenter_ClearsTransientData_ButKeepsDevices()
    {
        // Behavior fix: retuning to a DIFFERENT center frequency during a live capture must clear
        // the previous band's transient RF data (emitters/signals/spectrum/alerts) — it's stale and
        // confusing for the new band — but devices (persistent identity) and observations must NOT
        // be cleared.
        var app = factory.WithWebHostBuilder(b => { });
        var client = app.CreateClient();

        var wsClient = app.Server.CreateWebSocketClient();
        var uri = new UriBuilder(app.Server.BaseAddress) { Scheme = "ws", Path = "/ingest/iq" }.Uri;
        using var ws = await wsClient.ConnectAsync(uri, CancellationToken.None);

        // Initial config frame (center A). This is the ONE-TIME trigger for
        // ILiveSession.OnDeviceStreamStarted(), which clears every IDemoSeedStore.
        await ws.SendAsync(Encoding.UTF8.GetBytes(ConfigFrame(915_000_000L)),
            WebSocketMessageType.Text, true, CancellationToken.None);

        // Latch that one-time clear DETERMINISTICALLY before seeding below. LiveSession latches via
        // Interlocked, so whichever call arrives first wins and the other is a no-op — calling it
        // here guarantees the handler's own call cannot fire AFTER our seeding and wipe it.
        //
        // This replaces a Task.Delay(150) that merely HOPED the handler had got there first. It had
        // not, roughly 1 run in 12 under CI-like parallelism: the late clear deleted the emitter
        // seeded below and the first assertion saw 0. That was the CI failure on run 30231481804.
        app.Services.GetRequiredService<SignalAtlas.Persistence.ILiveSession>().OnDeviceStreamStarted();

        // Land data in every transient store directly via the shared singleton repos (mirrors how
        // the live pipeline would populate them during the first band's capture), plus a device to
        // prove it survives the retune.
        var emitters = app.Services.GetRequiredService<IEmitterRepository>();
        var signals = app.Services.GetRequiredService<ISignalWriter>();
        var alerts = app.Services.GetRequiredService<IAlertWriter>();
        var spectrum = app.Services.GetRequiredService<ISpectrumBuffer>();
        var devices = app.Services.GetRequiredService<IDeviceRepository>();

        emitters.Upsert(new Emitter(
            Id: "emitter-retune-test", DeviceId: null, Protocol: "LoRa",
            FreqCenterHz: 915_000_000, FreqStabilityHz: 5_000,
            EstLatitude: 42.36, EstLongitude: -71.06, EstUncertaintyM: 100.0,
            SignalCount: 1, Confidence: 0.8,
            Identifiers: new Dictionary<string, string> { ["devaddr"] = "AABBCCDD" },
            Evidence: [new EvidenceItem("modulation", "CSS", 0.5)]));
        signals.Add(new Signal(
            Id: 1, Time: DateTimeOffset.UtcNow, ObservationId: 1, EmitterId: null, DeviceId: null,
            Protocol: "LoRa", Confidence: 0.8, Classifier: "test",
            Evidence: [new EvidenceItem("modulation", "CSS", 0.5)],
            CenterFreqHz: 915_000_000, BandwidthHz: 125_000, DurationMs: 350,
            Features: new Dictionary<string, double>()));
        alerts.Add(new Alert(
            Id: Guid.NewGuid(), Time: DateTimeOffset.UtcNow, EmitterId: "emitter-retune-test",
            DeviceId: null, Kind: Alert.NewEmitter, Severity: "info", Summary: "retune test",
            Evidence: [new EvidenceItem("seed", "seed", 1.0)]));
        spectrum.Push(new SpectrumFrame(DateTimeOffset.UtcNow, 915_000_000, 2_000_000, [1.0, 2.0, 3.0]));
        devices.Upsert(new Device(
            Id: "4840D6", DeviceType: "Aircraft", PrimaryIdentifier: "4840D6",
            Identifiers: new Dictionary<string, string> { ["icao"] = "4840D6" }, Vendor: null,
            Protocol: "ADS-B", Confidence: 1.0, Evidence: [new EvidenceItem("crc", "pass", 1.0)]));

        Assert.True(await GetPayloadCount(client, "/api/v1/emitters") > 0);
        Assert.True(await GetPayloadCount(client, "/api/v1/devices") > 0);

        // Same-center config frame (gain-only change): must NOT clear anything.
        await ws.SendAsync(Encoding.UTF8.GetBytes(ConfigFrame(915_000_000L, vgaDb: 30)),
            WebSocketMessageType.Text, true, CancellationToken.None);

        // "Must not happen" cannot be polled for, so hold briefly and assert it stays populated.
        await AssertStaysPopulatedAsync(client, "/api/v1/emitters", "gain-only config frame cleared data");
        await AssertStaysPopulatedAsync(client, "/api/v1/signals", "gain-only config frame cleared signals");
        await AssertStaysPopulatedAsync(client, "/api/v1/alerts", "gain-only config frame cleared alerts");
        await AssertStaysPopulatedAsync(client, "/api/v1/spectrum/frames", "gain-only config frame cleared spectrum");

        // Retune to a DIFFERENT center: transient data must clear, devices must survive. The handler
        // clears on its own receive loop, so poll for each store to drain instead of sleeping 150 ms.
        await ws.SendAsync(Encoding.UTF8.GetBytes(ConfigFrame(433_920_000L)),
            WebSocketMessageType.Text, true, CancellationToken.None);

        await WaitForCountAsync(client, "/api/v1/emitters", c => c == 0, "a retune must clear emitters");
        await WaitForCountAsync(client, "/api/v1/signals", c => c == 0, "a retune must clear signals");
        await WaitForCountAsync(client, "/api/v1/alerts", c => c == 0, "a retune must clear alerts");
        await WaitForCountAsync(client, "/api/v1/spectrum/frames", c => c == 0, "a retune must clear spectrum frames");
        Assert.True(await GetPayloadCount(client, "/api/v1/devices") > 0, "devices must NOT be cleared on retune");

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
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

    [Fact]
    public async Task Streaming_ConfigWithReceiverConfig_RecordsProvenanceOnObservation()
    {
        // Wire-contract guard (landmine #1): the browser's config-frame RX fields
        // (ampEnable/lnaDb/vgaDb/basebandBwHz/biasTee) must deserialize into IqConfig and map onto a
        // ReceiverConfig on the persisted Observation. If ANY JsonPropertyName on IqConfig drifts from
        // the exact field names SdrProvider sends, the provenance goes silently null and this fails.
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

        // Field names here mirror SdrProvider.tsx's config frame verbatim.
        var config = JsonSerializer.Serialize(new
        {
            type = "config",
            centerFreqHz = 915_000_000L,
            sampleRateHz = 2_000_000,
            samplesPerBlock = 8,
            collectorId = "web-hackrf-test",
            ampEnable = true,
            lnaDb = 24,
            vgaDb = 30,
            basebandBwHz = 1_750_000,
            biasTee = true,
        });
        await ws.SendAsync(Encoding.UTF8.GetBytes(config), WebSocketMessageType.Text, true, CancellationToken.None);

        var iq = new byte[16];
        for (int i = 0; i < iq.Length; i++) iq[i] = (byte)(i % 2 == 0 ? 100 : 50);
        await ws.SendAsync(iq, WebSocketMessageType.Binary, true, CancellationToken.None);

        // The spectrum-frame broadcast signals the block was processed (Observation already written).
        var completed = await Task.WhenAny(notifier.First.Task, Task.Delay(TimeSpan.FromSeconds(5)));
        Assert.Same(notifier.First.Task, completed);
        await notifier.First.Task;

        var observations = app.Services.GetRequiredService<IObservationRepository>();
        ReceiverConfig? rx = null;
        for (int attempt = 0; attempt < 50 && rx is null; attempt++)
        {
            rx = observations.GetRecent(20).FirstOrDefault(o => o.ReceiverConfig is not null)?.ReceiverConfig;
            if (rx is null) await Task.Delay(20);
        }

        Assert.NotNull(rx);
        Assert.Equal(new ReceiverConfig(AmpEnable: true, LnaDb: 24, VgaDb: 30, BasebandBwHz: 1_750_000, BiasTee: true), rx);

        await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None);
    }
}
