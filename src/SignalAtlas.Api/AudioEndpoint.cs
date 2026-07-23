using System.Net.WebSockets;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using SignalAtlas.Processing;

namespace SignalAtlas.Api;

/// <summary>
/// Binary WebSocket egress for the RF Audio Player (design §3). Mapped OUTSIDE /api/v1 -- ungated,
/// loopback posture, alongside <c>/ingest/iq</c> and <c>/hub/live</c>. A text "config" frame
/// <c>{ "mode": "wbfm"|"nbfm"|"am"|"usb"|"lsb"|"cw", "enabled": true|false }</c> sets the shared <see cref="AudioHub"/>'s
/// active mode/enabled flag; the server then streams binary PCM16LE-mono frames (this connection's
/// <see cref="AudioHub.Subscriber"/> queue) until the socket closes. A client may send further config
/// frames at any time (e.g. switching mode while playing) -- config receipt and PCM send run as two
/// independent loops over the same socket (one pending receive + one pending send is safe on a single
/// <see cref="WebSocket"/>). The subscriber is always unregistered on close/disconnect/fault.
/// </summary>
public static class AudioEndpoint
{
    private const int MaxFrameBytes = 1 << 12; // Config frames are tiny JSON; generous 4 KiB guard.

    private sealed record AudioConfig(
        [property: JsonPropertyName("mode")] string? Mode,
        [property: JsonPropertyName("enabled")] bool Enabled);

    public static void MapAudio(this WebApplication app)
    {
        app.Map("/audio", async (HttpContext ctx) =>
        {
            if (!ctx.WebSockets.IsWebSocketRequest)
            {
                ctx.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await ctx.WebSockets.AcceptWebSocketAsync();
            var hub = ctx.RequestServices.GetRequiredService<AudioHub>();
            var requestAborted = ctx.RequestAborted;

            // A subscriber slot is held for the life of the connection -- PCM only actually flows
            // once a config frame enables the hub with a client connected (AudioHub.Accept is a
            // no-op otherwise), but the queue exists from connect so no early PCM is missed.
            var subscriber = hub.Register();

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
            try
            {
                var receiveTask = ReceiveConfigLoopAsync(socket, hub, linked.Token);
                var sendTask = SendPcmLoopAsync(socket, subscriber, linked.Token);
                await Task.WhenAny(receiveTask, sendTask);

                // Either loop ending (peer closed, fault, or cancellation) means the connection is
                // done -- stop the other loop and observe both so no exception goes unobserved.
                linked.Cancel();
                await SafeAwaitAsync(receiveTask);
                await SafeAwaitAsync(sendTask);
            }
            finally
            {
                hub.Unregister(subscriber);
                await CloseGracefullyAsync(socket, requestAborted);
            }
        });
    }

    private static async Task SafeAwaitAsync(Task task)
    {
        try { await task; }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ChannelClosedException) { }
    }

    private static async Task ReceiveConfigLoopAsync(WebSocket socket, AudioHub hub, CancellationToken ct)
    {
        var buffer = new byte[4096];
        while (!ct.IsCancellationRequested)
        {
            using var ms = new MemoryStream();
            WebSocketReceiveResult result;
            do
            {
                result = await socket.ReceiveAsync(buffer, ct);
                if (result.MessageType == WebSocketMessageType.Close)
                    return;
                ms.Write(buffer, 0, result.Count);
                if (ms.Length > MaxFrameBytes)
                    throw new WebSocketException("Frame exceeds maximum size.");
            }
            while (!result.EndOfMessage);

            if (result.MessageType != WebSocketMessageType.Text)
                continue; // Ignore stray binary frames from the client -- audio only flows server->client.

            var config = ParseConfig(ms.ToArray());
            if (config is null) continue;
            var mode = ParseMode(config.Mode);
            if (mode is null) continue;
            hub.Configure(mode.Value, config.Enabled);
        }
    }

    private static async Task SendPcmLoopAsync(WebSocket socket, AudioHub.Subscriber subscriber, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            byte[] pcm = await subscriber.ReadAsync(ct); // Throws ChannelClosedException once unregistered.
            if (pcm.Length == 0) continue;
            await socket.SendAsync(pcm, WebSocketMessageType.Binary, true, ct);
        }
    }

    private static AudioConfig? ParseConfig(byte[] utf8)
    {
        try { return JsonSerializer.Deserialize<AudioConfig>(utf8); }
        catch (JsonException) { return null; }
    }

    private static AudioMode? ParseMode(string? mode) => mode?.ToLowerInvariant() switch
    {
        "wbfm" => AudioMode.Wbfm,
        "nbfm" => AudioMode.Nbfm,
        "am" => AudioMode.Am,
        "usb" => AudioMode.Usb,
        "lsb" => AudioMode.Lsb,
        "cw" => AudioMode.Cw,
        _ => null,
    };

    // Mirrors IqIngressEndpoint.CloseGracefullyAsync: send-only close so a misbehaving/idle peer
    // can't pin the request open indefinitely waiting for a Close echo that never arrives.
    private static async Task CloseGracefullyAsync(WebSocket socket, CancellationToken ct)
    {
        var closeCt = ct.IsCancellationRequested ? CancellationToken.None : ct;
        try
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", closeCt);
        }
        catch (OperationCanceledException) { }
        catch (WebSocketException) { }
        catch (ObjectDisposedException) { }
    }
}
