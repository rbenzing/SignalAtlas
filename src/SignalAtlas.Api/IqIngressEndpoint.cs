using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using SignalAtlas.Collector;
using SignalAtlas.Domain;
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

            // First message must be the text config frame.
            var (kind, payload) = await ReceiveAsync(socket, ct);
            if (kind != WebSocketMessageType.Text)
                return;
            var config = ParseConfig(payload);
            if (config is null || config.SamplesPerBlock <= 0)
                return;

            var source = new BrowserUploadSampleSource(ChannelCapacity, config.SamplesPerBlock);
            var pipeline = BuildPipeline(ctx.RequestServices, config.CollectorId ?? "web-hackrf-1");
            var runTask = Task.Run(() => pipeline.Run(source, ct), ct);

            long centerHz = config.CenterFreqHz;
            int rateHz = config.SampleRateHz;
            try
            {
                while (!ct.IsCancellationRequested)
                {
                    var (msgKind, data) = await ReceiveAsync(socket, ct);
                    if (msgKind == WebSocketMessageType.Close)
                    {
                        // Complete the close handshake so the client's own CloseAsync/receive
                        // observes a graceful shutdown rather than an aborted connection.
                        if (socket.State == WebSocketState.CloseReceived)
                            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", ct);
                        break;
                    }
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
                source.Complete();
                try { await runTask; } catch (OperationCanceledException) { }
            }
        });
    }

    private static IqConfig? ParseConfig(byte[] utf8)
    {
        try { return JsonSerializer.Deserialize<IqConfig>(utf8); }
        catch (JsonException) { return null; }
    }

    // Mirrors PipelineHostedService's construction so every downstream artifact + SignalR event fans
    // out identically to file/synthetic ingestion.
    private static IngestionPipeline BuildPipeline(IServiceProvider sp, string collectorId) =>
        new(
            collectorId: collectorId,
            clock: sp.GetService<IClock>() ?? new HostClock(),
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
            notifier: sp.GetService<ILiveNotifier>());

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
