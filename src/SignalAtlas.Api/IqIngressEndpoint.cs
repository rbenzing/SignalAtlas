using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using SignalAtlas.Collector;
using SignalAtlas.Domain;
using SignalAtlas.Persistence;
using SignalAtlas.Pipeline;

namespace SignalAtlas.Api;

/// <summary>
/// Binary WebSocket ingress for a browser-owned HackRF (SPEC §4.10). Mapped OUTSIDE /api/v1 — an
/// intentional un-gated inbound surface alongside /hub/live (single-operator loopback posture).
/// A text "config" frame sets tuning; binary frames carry interleaved signed-8-bit I/Q. Frames feed
/// a <see cref="BrowserUploadSampleSource"/> driven through a per-connection <see cref="IngestionPipeline"/>.
/// </summary>
public static class IqIngressEndpoint
{
    private const int ChannelCapacity = 32;
    private const int MaxFrameBytes = 1 << 20; // 1 MiB guard.

    private sealed record IqConfig(
        [property: JsonPropertyName("centerFreqHz")] long CenterFreqHz,
        [property: JsonPropertyName("sampleRateHz")] int SampleRateHz,
        [property: JsonPropertyName("samplesPerBlock")] int SamplesPerBlock,
        [property: JsonPropertyName("collectorId")] string? CollectorId);

    public static void MapIqIngress(this WebApplication app)
    {
        app.Map("/ingest/iq", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var ct = ctx.RequestAborted;
            var logger = ctx.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("IqIngress");

            // Declared before the try so the finally can always reach them for cleanup, even when
            // one of the early-exit paths below (non-text/invalid-config/immediate-close) returns
            // before a pipeline is ever started.
            BrowserUploadSampleSource? source = null;
            Task? runTask = null;

            // Everything after AcceptWebSocketAsync — including the first ReceiveAsync — lives in
            // ONE try/finally so every exit path (bad first frame, immediate close, normal loop
            // close, or an abrupt disconnect) completes the close handshake exactly once.
            try
            {
                // First message must be the text config frame.
                var (kind, payload) = await ReceiveAsync(socket, ct);
                if (kind != WebSocketMessageType.Text)
                    return;
                var config = ParseConfig(payload);
                if (config is null || config.SamplesPerBlock <= 0)
                    return;

                // A real device is now streaming: clear the demo seed once so the UI shows live data
                // only (no-op after the first stream, and a no-op in DB mode).
                ctx.RequestServices.GetService<ILiveSession>()?.OnDeviceStreamStarted();

                source = new BrowserUploadSampleSource(ChannelCapacity, config.SamplesPerBlock);
                var pipeline = BuildPipeline(ctx.RequestServices, config.CollectorId ?? "web-hackrf-1");
                runTask = Task.Run(() => pipeline.Run(source, ct), ct);

                long centerHz = config.CenterFreqHz;
                int rateHz = config.SampleRateHz;

                while (!ct.IsCancellationRequested)
                {
                    var (msgKind, data) = await ReceiveAsync(socket, ct);
                    if (msgKind == WebSocketMessageType.Close)
                        break;
                    if (msgKind == WebSocketMessageType.Binary)
                    {
                        source.Enqueue(data, centerHz, rateHz);
                    }
                    else if (msgKind == WebSocketMessageType.Text)
                    {
                        var updated = ParseConfig(data);
                        if (updated is not null)
                        {
                            centerHz = updated.CenterFreqHz;
                            rateHz = updated.SampleRateHz;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { /* client vanished */ }
            catch (WebSocketException) { /* abrupt close */ }
            finally
            {
                source?.Complete();
                if (runTask is not null)
                {
                    try { await runTask; }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        // Observe non-cancellation pipeline faults so they don't escape the finally
                        // and mask whatever exit condition brought us here.
                        logger.LogWarning(ex, "IQ ingest pipeline faulted");
                    }
                }

                // Single graceful-close path for every exit above (early return, loop-break on
                // Close, or a caught disconnect exception).
                await CloseGracefullyAsync(socket, ct);
            }
        });
    }

    private static async Task CloseGracefullyAsync(WebSocket socket, CancellationToken ct)
    {
        // A cancelled token would make CloseOutputAsync throw immediately; fall back to
        // CancellationToken.None so a graceful close is still attempted on client-initiated
        // disconnect / request-abort. CloseOutputAsync only sends our close frame — it never
        // blocks waiting for the peer's Close echo, so a misbehaving/idle client (e.g. one that
        // sent a non-text or invalid-config first frame and then went quiet) can't pin this
        // request open indefinitely.
        var closeCt = ct.IsCancellationRequested ? CancellationToken.None : ct;
        try
        {
            if (socket.State == WebSocketState.Open)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", closeCt);
            else if (socket.State == WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", closeCt);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
    }

    private static IqConfig? ParseConfig(byte[] utf8)
    {
        try { return JsonSerializer.Deserialize<IqConfig>(utf8); }
        catch (JsonException) { return null; }
    }

    // Cap the live spectrum broadcast so a continuous WebUSB stream (hundreds of frames/s) can't flood
    // SignalR clients and freeze the browser. ~20 fps is smooth for a waterfall; only the live push is
    // throttled — persisted artifacts and the REST spectrum buffer still see every frame.
    private static readonly TimeSpan SpectrumPushInterval = TimeSpan.FromMilliseconds(50);

    // Mirrors PipelineHostedService's construction so every downstream artifact + SignalR event fans
    // out identically to file/synthetic ingestion.
    private static IngestionPipeline BuildPipeline(IServiceProvider sp, string collectorId)
    {
        var clock = sp.GetService<IClock>() ?? new HostClock();
        var liveNotifier = sp.GetService<ILiveNotifier>();
        // Throttle only the high-rate spectrum push; other events still fan out unthrottled.
        var notifier = liveNotifier is null
            ? null
            : new ThrottledSpectrumNotifier(liveNotifier, clock, SpectrumPushInterval);

        return new IngestionPipeline(
            collectorId: collectorId,
            clock: clock,
            position: sp.GetService<IPositionSource>() ?? new NoPositionSource(),
            processor: sp.GetRequiredService<ISignalProcessor>(),
            classifier: sp.GetRequiredService<IClassifier>(),
            correlation: sp.GetRequiredService<ICorrelationEngine>(),
            anomaly: sp.GetRequiredService<IAnomalyEngine>(),
            observations: sp.GetRequiredService<IObservationRepository>(),
            signals: sp.GetRequiredService<ISignalWriter>(),
            emitters: sp.GetService<IEmitterRepository>(),
            alerts: sp.GetService<IAlertWriter>(),
            spectrum: sp.GetService<ISpectrumBuffer>(),
            notifier: notifier,
            demodulators: sp.GetServices<IDemodulator>(),
            registry: sp.GetService<IDecoderRegistry>(),
            resolver: sp.GetService<IDeviceResolver>(),
            devices: sp.GetService<IDeviceRepository>(),
            cpr: sp.GetService<ICprPositionResolver>());
    }

    private static async Task<(WebSocketMessageType Kind, byte[] Payload)> ReceiveAsync(
        WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[16384];
        using var ms = new MemoryStream();
        WebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer, ct);
            if (result.MessageType == WebSocketMessageType.Close)
                return (WebSocketMessageType.Close, Array.Empty<byte>());
            ms.Write(buffer, 0, result.Count);
            if (ms.Length > MaxFrameBytes)
                throw new WebSocketException("Frame exceeds maximum size.");
        }
        while (!result.EndOfMessage);
        return (result.MessageType, ms.ToArray());
    }
}
