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
}
