# WebUSB HackRF One → Backend IQ Ingress Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Let the browser own a locally-attached HackRF One over WebUSB (navbar connect + permission prompt), stream received int8 IQ to the .NET backend over a binary WebSocket, and drive the existing ingestion pipeline (spectrum + protocol classification) — browser as a pure USB tether.

**Architecture:** A new RX-only HackRF WebUSB driver in the browser reads interleaved signed-8-bit I/Q and streams it over a dedicated binary WebSocket (`/ingest/iq`). The backend endpoint feeds a channel-backed `BrowserUploadSampleSource : ISampleSource` into a per-connection `IngestionPipeline.Run`, so PSD/features/classify/correlate and all SignalR live events are reused verbatim. No DSP runs in the browser; the backend computes the spectrum.

**Tech Stack:** .NET 10 (minimal APIs, `System.Threading.Channels`, ASP.NET WebSockets), React 18 + TypeScript + MUI, WebUSB (`navigator.usb`), Vitest (new) for frontend unit tests.

## Global Constraints

- **Receive-only (invariant #1):** no transmit code path anywhere. Browser driver must expose no `tx`/`transmit`/`send` method; backend ingress implements `ISampleSource` (covered by `ISampleSource_ExposesNoTransmitMember`). Reflection/guard tests assert this on both ends.
- **Metadata-not-content / egress (invariant #3):** raw IQ is consumed into `IqBlock`/PSD and never persisted or forwarded; no new field name may contain the `EgressGuard` markers (`raw_iq`, `iq_sample`, `iq_blob`, `iqref`, `iq_ref`, `cleartext`, `payload_bytes`, `raw_samples`).
- **Auth (invariant #5):** `/ingest/iq` is mapped **outside** the `/api/v1` group (un-gated, like `/hub/live`). No `/api/v1` route may become ungated.
- **Wire format:** IQ frames are raw interleaved **signed-8-bit** `I,Q,I,Q…`, `2 × samplesPerBlock` bytes/frame; normalized backend-side via `(sbyte)b / 128f` (identical to `FileSampleSource`).
- **Defaults:** center 915 MHz, sample rate 2 MS/s, LNA 16 dB, VGA 20 dB, amp off, `samplesPerBlock` 8192.
- **.NET SDK 10 required** (no `global.json`). Frontend Vite dev server on 5173 proxies to backend on 5285 — keep in sync.
- **After writing code:** run backend build/tests and frontend build/lint (`tsc -b`) per project convention.

---

## File Structure

**Backend (new):**
- `src/SignalAtlas.Collector/BrowserUploadSampleSource.cs` — channel-backed `ISampleSource`; int8→`IqBlock`, drop-oldest, `Complete()`.
- `src/SignalAtlas.Api/IqIngressEndpoint.cs` — `MapIqIngress()` extension: WebSocket accept, config parsing, per-connection pipeline run.
- `tests/SignalAtlas.Tests.Unit/BrowserUploadSampleSourceTests.cs`
- `tests/SignalAtlas.Tests.Integration/IqIngressEndpointTests.cs`

**Backend (modified):**
- `src/SignalAtlas.Api/Program.cs` — add `app.UseWebSockets()` + `app.MapIqIngress()`.

**Frontend (new):**
- `web/src/sdr/hackrf.ts` — RX-only HackRF WebUSB driver.
- `web/src/sdr/iqSocket.ts` — WebSocket IQ client (config frame + binary send + backpressure).
- `web/src/sdr/SdrProvider.tsx` — device state context (reducer-based).
- `web/src/components/HackRfConnect.tsx` — navbar connect control.
- `web/vitest.config.ts`, `web/src/test/setup.ts` — test tooling.
- `web/src/sdr/hackrf.test.ts`, `web/src/sdr/iqSocket.test.ts`, `web/src/sdr/sdrReducer.test.ts`, `web/src/components/HackRfConnect.test.tsx`

**Frontend (modified):**
- `web/package.json` — Vitest devDeps + `test` script.
- `web/vite.config.ts` — add `/ingest` ws proxy.
- `web/src/main.tsx` — wrap in `SdrProvider`.
- `web/src/components/AppShell.tsx` — render `<HackRfConnect />` in the top bar.

---

## Task 1: `BrowserUploadSampleSource` (channel-backed ISampleSource)

**Files:**
- Create: `src/SignalAtlas.Collector/BrowserUploadSampleSource.cs`
- Test: `tests/SignalAtlas.Tests.Unit/BrowserUploadSampleSourceTests.cs`

**Interfaces:**
- Consumes: `ISampleSource` (`IEnumerable<IqBlock> Blocks()`), `IqBlock(long centerFreqHz, int sampleRateHz, float[] i, float[] q)` from `SignalAtlas.Domain`.
- Produces: `class BrowserUploadSampleSource : ISampleSource` with
  `BrowserUploadSampleSource(int capacity, int samplesPerBlock)`,
  `void Enqueue(ReadOnlyMemory<byte> int8Iq, long centerFreqHz, int sampleRateHz)`,
  `void Complete()`, `IEnumerable<IqBlock> Blocks()`.

- [ ] **Step 1: Write the failing test**

Create `tests/SignalAtlas.Tests.Unit/BrowserUploadSampleSourceTests.cs`:

```csharp
using SignalAtlas.Collector;
using SignalAtlas.Domain;

namespace SignalAtlas.Tests.Unit;

public class BrowserUploadSampleSourceTests
{
    [Fact]
    public void Enqueue_ConvertsInt8InterleavedToNormalizedIqBlock()
    {
        var source = new BrowserUploadSampleSource(capacity: 8, samplesPerBlock: 2);
        // Two samples: (I=127,Q=-128), (I=0,Q=64) as signed bytes.
        var bytes = new byte[] { 127, unchecked((byte)-128), 0, 64 };

        source.Enqueue(bytes, 915_000_000, 2_000_000);
        source.Complete();

        var block = Assert.Single(source.Blocks());
        Assert.Equal(915_000_000, block.CenterFreqHz);
        Assert.Equal(2_000_000, block.SampleRateHz);
        Assert.Equal(2, block.SampleCount);
        Assert.Equal(127 / 128f, block.I[0], 5);
        Assert.Equal(-128 / 128f, block.Q[0], 5);
        Assert.Equal(0f, block.I[1], 5);
        Assert.Equal(64 / 128f, block.Q[1], 5);
    }

    [Fact]
    public void Blocks_YieldsQueuedThenTerminatesOnComplete()
    {
        var source = new BrowserUploadSampleSource(capacity: 8, samplesPerBlock: 1);
        source.Enqueue(new byte[] { 10, 20 }, 100, 1000);
        source.Enqueue(new byte[] { 30, 40 }, 100, 1000);
        source.Complete();

        var blocks = source.Blocks().ToList();

        Assert.Equal(2, blocks.Count);
    }

    [Fact]
    public void Enqueue_DropsRemainderShorterThanOneBlock()
    {
        var source = new BrowserUploadSampleSource(capacity: 8, samplesPerBlock: 2);
        // 3 bytes = 1.5 samples: not even one full 2-sample block → nothing enqueued.
        source.Enqueue(new byte[] { 1, 2, 3 }, 100, 1000);
        source.Complete();

        Assert.Empty(source.Blocks());
    }

    [Fact]
    public void ExposesNoTransmitMember()
    {
        var members = typeof(BrowserUploadSampleSource).GetMembers()
            .Select(m => m.Name.ToLowerInvariant());
        Assert.DoesNotContain(members, n => n.Contains("transmit") || n.Contains("send") || n.Contains("tx"));
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test SignalAtlas.slnx --filter "FullyQualifiedName~BrowserUploadSampleSourceTests"`
Expected: FAIL — `BrowserUploadSampleSource` does not exist (compile error).

- [ ] **Step 3: Write minimal implementation**

Create `src/SignalAtlas.Collector/BrowserUploadSampleSource.cs`:

```csharp
using System.Threading.Channels;
using SignalAtlas.Domain;

namespace SignalAtlas.Collector;

/// <summary>
/// Receive-only <see cref="ISampleSource"/> fed by a browser WebUSB HackRF over the /ingest/iq
/// WebSocket. Converts interleaved signed-8-bit I/Q (HackRF native, identical to
/// <see cref="FileSampleSource"/>) into normalized <see cref="IqBlock"/>s and hands them to the
/// existing pipeline. Bounded drop-oldest: under backpressure the freshest samples win (live RF).
/// Raw IQ is never persisted here — it is consumed into blocks and discarded (egress discipline).
/// Exposes no transmit member (SPEC §4.2 L1).
/// </summary>
public sealed class BrowserUploadSampleSource : ISampleSource
{
    private readonly Channel<IqBlock> _channel;
    private readonly int _samplesPerBlock;

    public BrowserUploadSampleSource(int capacity, int samplesPerBlock)
    {
        if (capacity <= 0) throw new ArgumentOutOfRangeException(nameof(capacity));
        if (samplesPerBlock <= 0) throw new ArgumentOutOfRangeException(nameof(samplesPerBlock));
        _samplesPerBlock = samplesPerBlock;
        _channel = Channel.CreateBounded<IqBlock>(new BoundedChannelOptions(capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
    }

    /// <summary>Enqueue one WebSocket binary frame of interleaved signed-8-bit I/Q.</summary>
    public void Enqueue(ReadOnlyMemory<byte> int8Iq, long centerFreqHz, int sampleRateHz)
    {
        var span = int8Iq.Span;
        int totalSamples = span.Length / 2;
        int fullBlocks = totalSamples / _samplesPerBlock;

        for (int b = 0; b < fullBlocks; b++)
        {
            var i = new float[_samplesPerBlock];
            var q = new float[_samplesPerBlock];
            for (int s = 0; s < _samplesPerBlock; s++)
            {
                int idx = (b * _samplesPerBlock + s) * 2;
                i[s] = (sbyte)span[idx] / 128f;
                q[s] = (sbyte)span[idx + 1] / 128f;
            }
            _channel.Writer.TryWrite(new IqBlock(centerFreqHz, sampleRateHz, i, q));
        }
    }

    /// <summary>Signal end-of-stream (WebSocket closed): <see cref="Blocks"/> terminates.</summary>
    public void Complete() => _channel.Writer.TryComplete();

    public IEnumerable<IqBlock> Blocks()
    {
        var reader = _channel.Reader;
        while (true)
        {
            bool hasData;
            try
            {
                hasData = reader.WaitToReadAsync().AsTask().GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                yield break;
            }
            if (!hasData)
                yield break;
            while (reader.TryRead(out var block))
                yield return block;
        }
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test SignalAtlas.slnx --filter "FullyQualifiedName~BrowserUploadSampleSourceTests"`
Expected: PASS (4 tests).

- [ ] **Step 5: Commit**

```bash
git add src/SignalAtlas.Collector/BrowserUploadSampleSource.cs tests/SignalAtlas.Tests.Unit/BrowserUploadSampleSourceTests.cs
git commit -m "feat: add BrowserUploadSampleSource for browser WebUSB IQ ingress"
```

---

## Task 2: WebSocket ingress endpoint `/ingest/iq`

**Files:**
- Create: `src/SignalAtlas.Api/IqIngressEndpoint.cs`
- Modify: `src/SignalAtlas.Api/Program.cs` (add `app.UseWebSockets()` after `app.UseRouting()` at line 183, and `app.MapIqIngress()` near the hub mapping at line 468)
- Test: `tests/SignalAtlas.Tests.Integration/IqIngressEndpointTests.cs`

**Interfaces:**
- Consumes: `BrowserUploadSampleSource` (Task 1); `IngestionPipeline(...)` and `.Run(ISampleSource, CancellationToken)` from `SignalAtlas.Pipeline`; DI services exactly as `PipelineHostedService` resolves them (`IClock`, `IPositionSource`, `ISignalProcessor`, `IClassifier`, `ICorrelationEngine`, `IAnomalyEngine`, `IObservationRepository`, `ISignalWriter`, `IEmitterRepository?`, `IAlertWriter?`, `ISpectrumBuffer?`, `ILiveNotifier?`), plus `HostClock`, `NoPositionSource`.
- Produces: `public static class IqIngressEndpoint { public static void MapIqIngress(this WebApplication app); }` mapping `GET /ingest/iq` (un-gated).

- [ ] **Step 1: Write the failing test**

Create `tests/SignalAtlas.Tests.Integration/IqIngressEndpointTests.cs`. It boots the app with a capturing `ILiveNotifier`, connects a TestServer WebSocket, sends a config text frame + one binary IQ block, and asserts a `SpectrumFrame` is broadcast.

```csharp
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
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
```

> Note: `RemoveAll` needs `using Microsoft.Extensions.DependencyInjection.Extensions;`. Add it if the analyzer flags it.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test SignalAtlas.slnx --filter "FullyQualifiedName~IqIngressEndpointTests"`
Expected: FAIL — `/ingest/iq` is not mapped (connect throws / 404).

- [ ] **Step 3: Write minimal implementation**

Create `src/SignalAtlas.Api/IqIngressEndpoint.cs`:

```csharp
using System.Net.WebSockets;
using System.Text;
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
```

> `HostClock` and `NoPositionSource` are the same fallbacks `PipelineHostedService` uses (same namespace/assembly). If they are not directly referenceable from this file, add the matching `using` (they resolve from the Api project as `PipelineHostedService` does).

- [ ] **Step 4: Wire into Program.cs**

Modify `src/SignalAtlas.Api/Program.cs`.

After `app.UseRouting();` (line 183), add:

```csharp
// WebSockets middleware for the browser HackRF IQ ingress (/ingest/iq).
app.UseWebSockets();
```

After `app.MapHub<LiveHub>("/hub/live");` (line 468), add:

```csharp
// Binary WebSocket IQ ingress for a browser-owned HackRF (SPEC §4.10). Un-gated, like /hub/live.
app.MapIqIngress();
```

- [ ] **Step 5: Run test to verify it passes**

Run: `dotnet test SignalAtlas.slnx --filter "FullyQualifiedName~IqIngressEndpointTests"`
Expected: PASS.

- [ ] **Step 6: Run the receive-only + full unit suite to confirm no regression**

Run: `dotnet test SignalAtlas.slnx --filter "Category!=NeedsDocker"`
Expected: PASS (including `ReceiveOnlyTests` and the route-gated contract test — `/ingest/iq` is outside `/api/v1`, so the gate test is unaffected).

- [ ] **Step 7: Commit**

```bash
git add src/SignalAtlas.Api/IqIngressEndpoint.cs src/SignalAtlas.Api/Program.cs tests/SignalAtlas.Tests.Integration/IqIngressEndpointTests.cs
git commit -m "feat: add /ingest/iq WebSocket endpoint feeding the ingestion pipeline"
```

---

## Task 3: Frontend test tooling (Vitest)

**Files:**
- Create: `web/vitest.config.ts`, `web/src/test/setup.ts`
- Modify: `web/package.json`

**Interfaces:**
- Produces: an `npm test` script (`vitest run`) with jsdom + jest-dom matchers, consumed by Tasks 4–7.

- [ ] **Step 1: Add dev dependencies**

Run:
```bash
cd web && npm install -D vitest@^2 jsdom@^25 @testing-library/react@^16 @testing-library/jest-dom@^6 @testing-library/user-event@^14
```

- [ ] **Step 2: Create the Vitest config**

Create `web/vitest.config.ts`:

```ts
import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  test: {
    environment: "jsdom",
    globals: true,
    setupFiles: ["./src/test/setup.ts"],
    include: ["src/**/*.{test,spec}.{ts,tsx}"],
  },
});
```

- [ ] **Step 3: Create the setup file**

Create `web/src/test/setup.ts`:

```ts
import "@testing-library/jest-dom";
```

- [ ] **Step 4: Add the test script**

In `web/package.json`, add to `"scripts"`:

```json
    "test": "vitest run",
    "test:watch": "vitest"
```

- [ ] **Step 5: Verify the runner works with a throwaway test**

Create `web/src/test/smoke.test.ts`:

```ts
import { describe, it, expect } from "vitest";

describe("tooling", () => {
  it("runs", () => {
    expect(1 + 1).toBe(2);
  });
});
```

Run: `cd web && npm test`
Expected: PASS (1 test). Then delete `web/src/test/smoke.test.ts`.

- [ ] **Step 6: Commit**

```bash
git add web/package.json web/package-lock.json web/vitest.config.ts web/src/test/setup.ts
git commit -m "chore(web): add Vitest unit-test tooling"
```

---

## Task 4: RX-only HackRF WebUSB driver

**Files:**
- Create: `web/src/sdr/hackrf.ts`
- Test: `web/src/sdr/hackrf.test.ts`

**Interfaces:**
- Produces:
  - `interface HackRfDeviceInfo { boardId: number; firmwareVersion: string; serialNumber: string; }`
  - `class HackRfDevice` with `get isConnected: boolean`, `get usbDevice: any`,
    `connect(): Promise<HackRfDeviceInfo>`, `adopt(device: any): Promise<HackRfDeviceInfo>`,
    `disconnect(): Promise<void>`, `setFrequency(hz)`, `setSampleRate(hz)`, `setBasebandFilter(hz)`,
    `setLnaGain(db)`, `setVgaGain(db)`, `setAmpEnable(on: boolean)`,
    `startRx(onData: (s: Int8Array) => void, onEnd?: (e?: Error) => void): Promise<void>`, `stop(): Promise<void>`.
  - `const HACKRF_FILTERS` (for `navigator.usb.requestDevice`/`getDevices`).

- [ ] **Step 1: Write the failing test (no-TX guard + baseband rounding)**

Create `web/src/sdr/hackrf.test.ts`:

```ts
import { describe, it, expect } from "vitest";
import { HackRfDevice, computeBasebandFilterBw, HACKRF_FILTERS } from "./hackrf";

describe("HackRfDevice — receive-only", () => {
  it("exposes no transmit/tx/send member (invariant #1)", () => {
    const names = Object.getOwnPropertyNames(HackRfDevice.prototype).map((n) => n.toLowerCase());
    const forbidden = names.filter((n) => /transmit|tx|send/.test(n));
    expect(forbidden).toEqual([]);
  });

  it("advertises the HackRF vendor/product filters", () => {
    expect(HACKRF_FILTERS).toContainEqual({ vendorId: 0x1d50, productId: 0x6089 });
  });

  it("rounds a requested baseband bandwidth down to a valid MAX2837 value", () => {
    expect(computeBasebandFilterBw(2_200_000)).toBe(1_750_000);
    expect(computeBasebandFilterBw(2_500_000)).toBe(2_500_000);
    expect(computeBasebandFilterBw(999_999_999)).toBe(28_000_000);
  });
});
```

> Note: `setTransceiverMode` does not match `/transmit|tx|send/` ("transceiver" contains no "tx"/"send"), so it is allowed; it is `private` anyway.

- [ ] **Step 2: Run test to verify it fails**

Run: `cd web && npm test -- hackrf`
Expected: FAIL — module `./hackrf` not found.

- [ ] **Step 3: Write the implementation**

Create `web/src/sdr/hackrf.ts` (adapted from signal-weaver `hackrf-usb.ts` with **all transmit code removed**: no `SET_TXVGA_GAIN`, no `startTx`, no `setTxVgaGain`, `TransceiverMode` limited to `OFF|RX`):

```ts
/**
 * HackRF One WebUSB protocol layer — RECEIVE ONLY.
 * No transmit request code, no TX mode, no TX gain (Signal Atlas invariant #1).
 */

export interface HackRfDeviceInfo {
  boardId: number;
  firmwareVersion: string;
  serialNumber: string;
}

const HACKRF_VENDOR_ID = 0x1d50;
const HACKRF_PRODUCT_IDS = [0x6089, 0x604b];

export const HACKRF_FILTERS = HACKRF_PRODUCT_IDS.map((productId) => ({
  vendorId: HACKRF_VENDOR_ID,
  productId,
}));

const VALID_BASEBAND_BW = [
  1750000, 2500000, 3500000, 5000000, 5500000, 6000000, 7000000, 8000000,
  9000000, 10000000, 12000000, 14000000, 15000000, 20000000, 24000000, 28000000,
];

export function computeBasebandFilterBw(requestedHz: number): number {
  let best = VALID_BASEBAND_BW[0];
  for (const bw of VALID_BASEBAND_BW) {
    if (bw <= requestedHz) best = bw;
    else break;
  }
  return best;
}

// Receive-only subset of the HackRF vendor request codes (21 = SET_TXVGA_GAIN is deliberately absent).
enum HackRfRequest {
  SET_TRANSCEIVER_MODE = 1,
  SAMPLE_RATE_SET = 6,
  BASEBAND_FILTER_BANDWIDTH_SET = 7,
  BOARD_ID_READ = 14,
  VERSION_STRING_READ = 15,
  SET_FREQ = 16,
  AMP_ENABLE = 17,
  BOARD_PARTID_SERIALNO_READ = 18,
  SET_LNA_GAIN = 19,
  SET_VGA_GAIN = 20,
}

// TransceiverMode has no TX member: only OFF and RX exist in this build.
enum TransceiverMode {
  OFF = 0,
  RX = 1,
}

// eslint-disable-next-line @typescript-eslint/no-explicit-any
type AnyUSBDevice = any;

export class HackRfDevice {
  private device: AnyUSBDevice = null;
  private bulkInEndpoint = 0;
  private streaming = false;
  private onData: ((samples: Int8Array) => void) | null = null;
  private onEnd: ((error?: Error) => void) | null = null;
  private interfaceNumber = 0;

  get isConnected(): boolean {
    return this.device !== null && this.device.opened;
  }

  get usbDevice(): AnyUSBDevice {
    return this.device;
  }

  /** Prompt the browser device chooser, then open the selected HackRF. */
  async connect(): Promise<HackRfDeviceInfo> {
    const nav = navigator as Navigator & { usb?: USB };
    if (!nav.usb) {
      throw new Error(
        "WebUSB is not supported. Use Chrome or Edge. If the HackRF shows as a COM port, " +
          "install the WinUSB driver with Zadig (https://zadig.akeo.ie/).",
      );
    }
    try {
      this.device = await nav.usb.requestDevice({ filters: HACKRF_FILTERS });
    } catch (error) {
      if ((error as Error).name === "NotFoundError") {
        throw new Error(
          "No HackRF selected. Ensure it is plugged in, in HackRF mode, the WinUSB driver is " +
            "installed (Zadig), and no other SDR software is using it.",
        );
      }
      throw error;
    }
    return this.open();
  }

  /** Open a device already granted via navigator.usb.getDevices() (silent reconnect). */
  async adopt(device: AnyUSBDevice): Promise<HackRfDeviceInfo> {
    this.device = device;
    return this.open();
  }

  private async open(): Promise<HackRfDeviceInfo> {
    if (!this.device) throw new Error("No device selected.");
    await this.device.open();
    if (this.device.configuration === null) await this.device.selectConfiguration(1);

    const iface = this.device.configuration.interfaces[0];
    this.interfaceNumber = iface.interfaceNumber;
    await this.device.claimInterface(this.interfaceNumber);

    this.bulkInEndpoint = 0;
    for (const ep of iface.alternates[0].endpoints) {
      if (ep.type === "bulk" && ep.direction === "in") {
        this.bulkInEndpoint = ep.endpointNumber;
        break;
      }
    }
    if (!this.bulkInEndpoint) this.bulkInEndpoint = 1;

    return this.readDeviceInfo();
  }

  async disconnect(): Promise<void> {
    await this.stop();
    if (this.device) {
      try { await this.device.releaseInterface(this.interfaceNumber); } catch { /* ignore */ }
      try { await this.device.close(); } catch { /* ignore */ }
      this.device = null;
    }
  }

  private async controlIn(request: number, value: number, length: number, index = 0): Promise<DataView> {
    if (!this.device) throw new Error("Not connected");
    const result = await this.device.controlTransferIn(
      { requestType: "vendor", recipient: "device", request, value, index },
      length,
    );
    if (result.status !== "ok" || !result.data)
      throw new Error(`Control IN failed: request=${request} status=${result.status}`);
    return result.data;
  }

  private async controlOut(request: number, value: number, data?: BufferSource): Promise<void> {
    if (!this.device) throw new Error("Not connected");
    const result = await this.device.controlTransferOut(
      { requestType: "vendor", recipient: "device", request, value, index: 0 },
      data,
    );
    if (result.status !== "ok")
      throw new Error(`Control OUT failed: request=${request} status=${result.status}`);
  }

  private async readDeviceInfo(): Promise<HackRfDeviceInfo> {
    let boardId = 0;
    try { boardId = (await this.controlIn(HackRfRequest.BOARD_ID_READ, 0, 1)).getUint8(0); } catch { /* ignore */ }

    let firmwareVersion = "Unknown";
    try {
      const d = await this.controlIn(HackRfRequest.VERSION_STRING_READ, 0, 255);
      firmwareVersion = new TextDecoder().decode(d.buffer).replace(/\0+$/, "");
    } catch { /* ignore */ }

    let serialNumber = "Unknown";
    try {
      const d = await this.controlIn(HackRfRequest.BOARD_PARTID_SERIALNO_READ, 0, 24);
      const parts: string[] = [];
      for (let i = 8; i < 24; i += 4) parts.push(d.getUint32(i, true).toString(16).padStart(8, "0"));
      serialNumber = parts.join("").toUpperCase();
    } catch { /* ignore */ }

    return { boardId, firmwareVersion, serialNumber };
  }

  async setFrequency(freqHz: number): Promise<void> {
    const data = new ArrayBuffer(8);
    const view = new DataView(data);
    view.setUint32(0, Math.floor(freqHz / 1e6), true);
    view.setUint32(4, Math.floor(freqHz % 1e6), true);
    await this.controlOut(HackRfRequest.SET_FREQ, 0, data);
  }

  async setSampleRate(rateHz: number): Promise<void> {
    const data = new ArrayBuffer(8);
    const view = new DataView(data);
    view.setUint32(0, Math.floor(rateHz), true);
    view.setUint32(4, 1, true);
    await this.controlOut(HackRfRequest.SAMPLE_RATE_SET, 0, data);
  }

  async setBasebandFilter(bwHz: number): Promise<void> {
    const bw = computeBasebandFilterBw(Math.floor(bwHz));
    if (!this.device) throw new Error("Not connected");
    const result = await this.device.controlTransferOut({
      requestType: "vendor",
      recipient: "device",
      request: HackRfRequest.BASEBAND_FILTER_BANDWIDTH_SET,
      value: bw & 0xffff,
      index: (bw >> 16) & 0xffff,
    });
    if (result.status !== "ok") throw new Error(`setBasebandFilter failed: ${result.status}`);
  }

  async setLnaGain(gain: number): Promise<void> {
    const rounded = Math.min(40, Math.max(0, gain)) & ~0x07;
    await this.controlIn(HackRfRequest.SET_LNA_GAIN, 0, 1, rounded);
  }

  async setVgaGain(gain: number): Promise<void> {
    const rounded = Math.min(62, Math.max(0, gain)) & ~0x01;
    await this.controlIn(HackRfRequest.SET_VGA_GAIN, 0, 1, rounded);
  }

  async setAmpEnable(enabled: boolean): Promise<void> {
    await this.controlOut(HackRfRequest.AMP_ENABLE, enabled ? 1 : 0);
  }

  private async setTransceiverMode(mode: TransceiverMode): Promise<void> {
    await this.controlOut(HackRfRequest.SET_TRANSCEIVER_MODE, mode);
  }

  async startRx(onData: (samples: Int8Array) => void, onEnd?: (error?: Error) => void): Promise<void> {
    if (this.streaming) return;
    this.onData = onData;
    this.onEnd = onEnd ?? null;
    this.streaming = true;
    await this.setTransceiverMode(TransceiverMode.RX);
    void this.readLoop();
  }

  async stop(): Promise<void> {
    this.streaming = false;
    this.onEnd = null;
    try { await this.setTransceiverMode(TransceiverMode.OFF); } catch { /* ignore */ }
  }

  private async readLoop(): Promise<void> {
    while (this.streaming && this.device?.opened) {
      try {
        const result = await this.device.transferIn(this.bulkInEndpoint, 16384);
        if (result.status === "ok") {
          if (result.data && result.data.byteLength > 0) {
            const dv = result.data;
            this.onData?.(new Int8Array(dv.buffer, dv.byteOffset, dv.byteLength));
          }
        } else if (result.status === "stall" || result.status === "babble") {
          try { await this.device.clearHalt("in", this.bulkInEndpoint); } catch { /* ignore */ }
          await new Promise((resolve) => setTimeout(resolve, 2));
        }
      } catch (error) {
        if (this.streaming) {
          this.streaming = false;
          const notify = this.onEnd;
          this.onEnd = null;
          notify?.(error as Error);
        }
        break;
      }
    }
  }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd web && npm test -- hackrf`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add web/src/sdr/hackrf.ts web/src/sdr/hackrf.test.ts
git commit -m "feat(web): add RX-only HackRF WebUSB driver"
```

---

## Task 5: `iqSocket` — config frame, binary send, backpressure

**Files:**
- Create: `web/src/sdr/iqSocket.ts`
- Test: `web/src/sdr/iqSocket.test.ts`

**Interfaces:**
- Produces:
  - `interface IqStreamConfig { type: "config"; centerFreqHz: number; sampleRateHz: number; samplesPerBlock: number; collectorId: string; }`
  - `interface IqSocketLike { readonly bufferedAmount: number; send(data: string | ArrayBufferView): void; }`
  - `class IqSocket` with `constructor(socket: IqSocketLike, maxBufferedBytes?: number)`,
    `sendConfig(cfg: IqStreamConfig): void`, `sendIq(samples: Int8Array): boolean` (returns false when dropped),
    `get drops: number`.
- Consumed by: `SdrProvider` (Task 6). `SdrProvider` constructs the real `WebSocket` and passes it in; tests pass a fake.

- [ ] **Step 1: Write the failing test**

Create `web/src/sdr/iqSocket.test.ts`:

```ts
import { describe, it, expect, vi } from "vitest";
import { IqSocket, type IqSocketLike, type IqStreamConfig } from "./iqSocket";

function fakeSocket(bufferedAmount = 0): IqSocketLike & { sent: Array<string | ArrayBufferView> } {
  return { bufferedAmount, sent: [], send(data) { this.sent.push(data); } };
}

const cfg: IqStreamConfig = {
  type: "config", centerFreqHz: 915e6, sampleRateHz: 2e6, samplesPerBlock: 8192, collectorId: "web-hackrf-1",
};

describe("IqSocket", () => {
  it("sends the config as a JSON text frame", () => {
    const ws = fakeSocket();
    new IqSocket(ws).sendConfig(cfg);
    expect(JSON.parse(ws.sent[0] as string)).toEqual(cfg);
  });

  it("sends IQ as a binary frame when buffer is below the cap", () => {
    const ws = fakeSocket(0);
    const ok = new IqSocket(ws, 1_000_000).sendIq(new Int8Array([1, 2, 3, 4]));
    expect(ok).toBe(true);
    expect(ws.sent[0]).toBeInstanceOf(Int8Array);
  });

  it("drops (does not send) IQ when bufferedAmount exceeds the cap", () => {
    const ws = fakeSocket(2_000_000);
    const sock = new IqSocket(ws, 1_000_000);
    const ok = sock.sendIq(new Int8Array([1, 2, 3, 4]));
    expect(ok).toBe(false);
    expect(ws.sent).toHaveLength(0);
    expect(sock.drops).toBe(1);
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd web && npm test -- iqSocket`
Expected: FAIL — module `./iqSocket` not found.

- [ ] **Step 3: Write the implementation**

Create `web/src/sdr/iqSocket.ts`:

```ts
export interface IqStreamConfig {
  type: "config";
  centerFreqHz: number;
  sampleRateHz: number;
  samplesPerBlock: number;
  collectorId: string;
}

/** Minimal surface of the browser WebSocket we depend on (so tests can fake it). */
export interface IqSocketLike {
  readonly bufferedAmount: number;
  send(data: string | ArrayBufferView): void;
}

const DEFAULT_MAX_BUFFERED_BYTES = 1_000_000; // ~0.25 s at 2 MS/s int8 I/Q; drop-oldest beyond this.

/**
 * Frames IQ over a WebSocket: one JSON text config frame, then binary int8 frames. Applies
 * drop-oldest backpressure — if the socket's send buffer is backed up, the freshest block is
 * dropped rather than queued unboundedly (mirrors the backend BoundedChannel DropOldest).
 */
export class IqSocket {
  private _drops = 0;

  constructor(
    private readonly socket: IqSocketLike,
    private readonly maxBufferedBytes = DEFAULT_MAX_BUFFERED_BYTES,
  ) {}

  get drops(): number {
    return this._drops;
  }

  sendConfig(cfg: IqStreamConfig): void {
    this.socket.send(JSON.stringify(cfg));
  }

  /** Returns true if sent, false if dropped due to backpressure. */
  sendIq(samples: Int8Array): boolean {
    if (this.socket.bufferedAmount > this.maxBufferedBytes) {
      this._drops++;
      return false;
    }
    this.socket.send(samples);
    return true;
  }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd web && npm test -- iqSocket`
Expected: PASS (3 tests).

- [ ] **Step 5: Commit**

```bash
git add web/src/sdr/iqSocket.ts web/src/sdr/iqSocket.test.ts
git commit -m "feat(web): add IqSocket framing + backpressure"
```

---

## Task 6: `SdrProvider` — device state context (reducer)

**Files:**
- Create: `web/src/sdr/SdrProvider.tsx`
- Test: `web/src/sdr/sdrReducer.test.ts`

**Interfaces:**
- Consumes: `HackRfDevice`, `HackRfDeviceInfo` (Task 4); `IqSocket`, `IqStreamConfig` (Task 5).
- Produces:
  - `type SdrStatus = "idle" | "requesting" | "streaming" | "error";`
  - `interface SdrTuning { centerFreqHz: number; sampleRateHz: number; lnaGain: number; vgaGain: number; ampEnable: boolean; }`
  - `interface SdrState { status: SdrStatus; serial: string | null; firmware: string | null; error: string | null; tuning: SdrTuning; drops: number; }`
  - `const DEFAULT_TUNING: SdrTuning` and `const INITIAL_SDR_STATE: SdrState`
  - `type SdrAction` and `function sdrReducer(state, action): SdrState` (pure — the unit-tested surface)
  - `function SdrProvider({ children })` and `function useSdr(): SdrContextValue` where
    `SdrContextValue = SdrState & { connect(): Promise<void>; disconnect(): Promise<void>; setTuning(patch: Partial<SdrTuning>): Promise<void>; }`

- [ ] **Step 1: Write the failing test (pure reducer)**

Create `web/src/sdr/sdrReducer.test.ts`:

```ts
import { describe, it, expect } from "vitest";
import { sdrReducer, INITIAL_SDR_STATE, DEFAULT_TUNING } from "./SdrProvider";

describe("sdrReducer", () => {
  it("moves idle → requesting on connect/request", () => {
    const s = sdrReducer(INITIAL_SDR_STATE, { type: "requesting" });
    expect(s.status).toBe("requesting");
    expect(s.error).toBeNull();
  });

  it("records device info and enters streaming on connected", () => {
    const s = sdrReducer(INITIAL_SDR_STATE, {
      type: "connected",
      info: { boardId: 2, firmwareVersion: "2024.02.1", serialNumber: "ABC123" },
    });
    expect(s.status).toBe("streaming");
    expect(s.serial).toBe("ABC123");
    expect(s.firmware).toBe("2024.02.1");
  });

  it("captures an error message and returns to error status", () => {
    const s = sdrReducer(INITIAL_SDR_STATE, { type: "error", message: "No HackRF selected." });
    expect(s.status).toBe("error");
    expect(s.error).toBe("No HackRF selected.");
  });

  it("resets to idle on disconnect", () => {
    const streaming = sdrReducer(INITIAL_SDR_STATE, {
      type: "connected",
      info: { boardId: 2, firmwareVersion: "x", serialNumber: "S" },
    });
    const s = sdrReducer(streaming, { type: "disconnected" });
    expect(s.status).toBe("idle");
    expect(s.serial).toBeNull();
    expect(s.tuning).toEqual(DEFAULT_TUNING);
  });

  it("merges a tuning patch without changing status", () => {
    const s = sdrReducer(INITIAL_SDR_STATE, { type: "tuning", patch: { centerFreqHz: 433_920_000 } });
    expect(s.tuning.centerFreqHz).toBe(433_920_000);
    expect(s.tuning.sampleRateHz).toBe(DEFAULT_TUNING.sampleRateHz);
    expect(s.status).toBe("idle");
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd web && npm test -- sdrReducer`
Expected: FAIL — module `./SdrProvider` not found.

- [ ] **Step 3: Write the implementation**

Create `web/src/sdr/SdrProvider.tsx`:

```tsx
import { createContext, useCallback, useContext, useReducer, useRef } from "react";
import { HackRfDevice, HACKRF_FILTERS, type HackRfDeviceInfo } from "./hackrf";
import { IqSocket, type IqStreamConfig } from "./iqSocket";

export type SdrStatus = "idle" | "requesting" | "streaming" | "error";

export interface SdrTuning {
  centerFreqHz: number;
  sampleRateHz: number;
  lnaGain: number;
  vgaGain: number;
  ampEnable: boolean;
}

export const DEFAULT_TUNING: SdrTuning = {
  centerFreqHz: 915_000_000,
  sampleRateHz: 2_000_000,
  lnaGain: 16,
  vgaGain: 20,
  ampEnable: false,
};

export interface SdrState {
  status: SdrStatus;
  serial: string | null;
  firmware: string | null;
  error: string | null;
  tuning: SdrTuning;
  drops: number;
}

export const INITIAL_SDR_STATE: SdrState = {
  status: "idle",
  serial: null,
  firmware: null,
  error: null,
  tuning: DEFAULT_TUNING,
  drops: 0,
};

export type SdrAction =
  | { type: "requesting" }
  | { type: "connected"; info: HackRfDeviceInfo }
  | { type: "error"; message: string }
  | { type: "disconnected" }
  | { type: "tuning"; patch: Partial<SdrTuning> }
  | { type: "drops"; drops: number };

export function sdrReducer(state: SdrState, action: SdrAction): SdrState {
  switch (action.type) {
    case "requesting":
      return { ...state, status: "requesting", error: null };
    case "connected":
      return {
        ...state,
        status: "streaming",
        serial: action.info.serialNumber,
        firmware: action.info.firmwareVersion,
        error: null,
      };
    case "error":
      return { ...state, status: "error", error: action.message };
    case "disconnected":
      return { ...INITIAL_SDR_STATE };
    case "tuning":
      return { ...state, tuning: { ...state.tuning, ...action.patch } };
    case "drops":
      return { ...state, drops: action.drops };
    default:
      return state;
  }
}

type SdrContextValue = SdrState & {
  connect: () => Promise<void>;
  disconnect: () => Promise<void>;
  setTuning: (patch: Partial<SdrTuning>) => Promise<void>;
};

const SdrContext = createContext<SdrContextValue | null>(null);

function iqSocketUrl(): string {
  const proto = window.location.protocol === "https:" ? "wss" : "ws";
  return `${proto}://${window.location.host}/ingest/iq`;
}

const SAMPLES_PER_BLOCK = 8192;

export function SdrProvider({ children }: { children: React.ReactNode }) {
  const [state, dispatch] = useReducer(sdrReducer, INITIAL_SDR_STATE);
  const deviceRef = useRef<HackRfDevice | null>(null);
  const wsRef = useRef<WebSocket | null>(null);
  const iqRef = useRef<IqSocket | null>(null);
  const tuningRef = useRef<SdrTuning>(DEFAULT_TUNING);

  const configFrame = (t: SdrTuning): IqStreamConfig => ({
    type: "config",
    centerFreqHz: t.centerFreqHz,
    sampleRateHz: t.sampleRateHz,
    samplesPerBlock: SAMPLES_PER_BLOCK,
    collectorId: "web-hackrf-1",
  });

  const applyTuningToDevice = async (dev: HackRfDevice, t: SdrTuning) => {
    await dev.setSampleRate(t.sampleRateHz);
    await dev.setBasebandFilter(t.sampleRateHz);
    await dev.setFrequency(t.centerFreqHz);
    await dev.setLnaGain(t.lnaGain);
    await dev.setVgaGain(t.vgaGain);
    await dev.setAmpEnable(t.ampEnable);
  };

  const startStreaming = async (info: HackRfDeviceInfo, dev: HackRfDevice) => {
    const t = tuningRef.current;
    await applyTuningToDevice(dev, t);

    const ws = new WebSocket(iqSocketUrl());
    ws.binaryType = "arraybuffer";
    wsRef.current = ws;
    const iq = new IqSocket(ws);
    iqRef.current = iq;

    await new Promise<void>((resolve, reject) => {
      ws.onopen = () => resolve();
      ws.onerror = () => reject(new Error("IQ WebSocket failed to open."));
    });
    iq.sendConfig(configFrame(t));

    await dev.startRx(
      (samples) => {
        iq.sendIq(samples);
        if (iq.drops !== state.drops) dispatch({ type: "drops", drops: iq.drops });
      },
      (err) => {
        if (err) dispatch({ type: "error", message: err.message });
      },
    );
    dispatch({ type: "connected", info });
  };

  const connect = useCallback(async () => {
    dispatch({ type: "requesting" });
    try {
      const dev = new HackRfDevice();
      deviceRef.current = dev;
      const info = await dev.connect();
      await startStreaming(info, dev);
    } catch (e) {
      dispatch({ type: "error", message: (e as Error).message });
      await disconnect();
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const disconnect = useCallback(async () => {
    try { await deviceRef.current?.disconnect(); } catch { /* ignore */ }
    try { wsRef.current?.close(); } catch { /* ignore */ }
    deviceRef.current = null;
    wsRef.current = null;
    iqRef.current = null;
    dispatch({ type: "disconnected" });
  }, []);

  const setTuning = useCallback(async (patch: Partial<SdrTuning>) => {
    const next = { ...tuningRef.current, ...patch };
    tuningRef.current = next;
    dispatch({ type: "tuning", patch });
    const dev = deviceRef.current;
    if (dev?.isConnected) {
      await applyTuningToDevice(dev, next);
      iqRef.current?.sendConfig(configFrame(next));
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const value: SdrContextValue = { ...state, connect, disconnect, setTuning };
  return <SdrContext.Provider value={value}>{children}</SdrContext.Provider>;
}

export function useSdr(): SdrContextValue {
  const ctx = useContext(SdrContext);
  if (!ctx) throw new Error("useSdr must be used within a SdrProvider");
  return ctx;
}

// Silence unused import in environments that tree-shake; HACKRF_FILTERS is used for future
// getDevices() silent reconnect wiring.
void HACKRF_FILTERS;
```

> Note: `useCallback` is imported as `useCallback` — correct the import to `import { createContext, useCallback, useContext, useReducer, useRef } from "react";` (the identifier is `useCallback`, lowercase c). Fix if the analyzer flags `useCallback` casing.

- [ ] **Step 4: Run test to verify it passes**

Run: `cd web && npm test -- sdrReducer`
Expected: PASS (5 tests).

- [ ] **Step 5: Commit**

```bash
git add web/src/sdr/SdrProvider.tsx web/src/sdr/sdrReducer.test.ts
git commit -m "feat(web): add SdrProvider device state context"
```

---

## Task 7: Navbar `HackRfConnect` control + app wiring

**Files:**
- Create: `web/src/components/HackRfConnect.tsx`
- Test: `web/src/components/HackRfConnect.test.tsx`
- Modify: `web/src/components/AppShell.tsx` (render `<HackRfConnect />` in the top bar), `web/src/main.tsx` (wrap in `SdrProvider`), `web/vite.config.ts` (add `/ingest` ws proxy)

**Interfaces:**
- Consumes: `useSdr()`, `SdrProvider` (Task 6).
- Produces: `export default function HackRfConnect()` — a navbar button/status.

- [ ] **Step 1: Write the failing test**

Create `web/src/components/HackRfConnect.test.tsx`:

```tsx
import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import HackRfConnect from "./HackRfConnect";
import * as sdr from "../sdr/SdrProvider";

describe("HackRfConnect", () => {
  it("shows a Connect HackRF button when idle and calls connect on click", async () => {
    const connect = vi.fn().mockResolvedValue(undefined);
    vi.spyOn(sdr, "useSdr").mockReturnValue({
      ...sdr.INITIAL_SDR_STATE,
      connect,
      disconnect: vi.fn(),
      setTuning: vi.fn(),
    });

    render(<HackRfConnect />);
    const button = screen.getByRole("button", { name: /connect hackrf/i });
    await userEvent.click(button);
    expect(connect).toHaveBeenCalledOnce();
  });

  it("shows the serial and a Disconnect action when streaming", () => {
    vi.spyOn(sdr, "useSdr").mockReturnValue({
      ...sdr.INITIAL_SDR_STATE,
      status: "streaming",
      serial: "ABC123",
      connect: vi.fn(),
      disconnect: vi.fn(),
      setTuning: vi.fn(),
    });

    render(<HackRfConnect />);
    expect(screen.getByText(/ABC123/)).toBeInTheDocument();
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd web && npm test -- HackRfConnect`
Expected: FAIL — module `./HackRfConnect` not found.

- [ ] **Step 3: Write the implementation**

Create `web/src/components/HackRfConnect.tsx`:

```tsx
import { useState } from "react";
import Button from "@mui/material/Button";
import Box from "@mui/material/Box";
import Chip from "@mui/material/Chip";
import IconButton from "@mui/material/IconButton";
import Popover from "@mui/material/Popover";
import Stack from "@mui/material/Stack";
import TextField from "@mui/material/TextField";
import FormControlLabel from "@mui/material/FormControlLabel";
import Switch from "@mui/material/Switch";
import Tooltip from "@mui/material/Tooltip";
import CircularProgress from "@mui/material/CircularProgress";
import SettingsInputAntennaIcon from "@mui/icons-material/SettingsInputAntenna";
import TuneIcon from "@mui/icons-material/Tune";
import { useSdr } from "../sdr/SdrProvider";

export default function HackRfConnect() {
  const sdr = useSdr();
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);

  if (sdr.status === "idle" || sdr.status === "error") {
    return (
      <Tooltip title={sdr.error ?? "Connect a HackRF One over WebUSB"}>
        <Button
          size="small"
          variant="outlined"
          color={sdr.status === "error" ? "error" : "primary"}
          startIcon={<SettingsInputAntennaIcon />}
          onClick={() => void sdr.connect()}
        >
          Connect HackRF
        </Button>
      </Tooltip>
    );
  }

  if (sdr.status === "requesting") {
    return <Chip icon={<CircularProgress size={14} />} label="Connecting…" size="small" />;
  }

  // streaming
  return (
    <Box sx={{ display: "flex", alignItems: "center", gap: 1 }}>
      <Chip
        color="success"
        variant="outlined"
        size="small"
        icon={<SettingsInputAntennaIcon />}
        label={`HackRF ${sdr.serial ?? ""}${sdr.drops > 0 ? ` · ${sdr.drops} drops` : ""}`}
      />
      <Tooltip title="Tuning">
        <IconButton size="small" onClick={(e) => setAnchor(e.currentTarget)} aria-label="HackRF tuning">
          <TuneIcon fontSize="small" />
        </IconButton>
      </Tooltip>
      <Button size="small" color="inherit" onClick={() => void sdr.disconnect()}>
        Disconnect
      </Button>

      <Popover
        open={Boolean(anchor)}
        anchorEl={anchor}
        onClose={() => setAnchor(null)}
        anchorOrigin={{ vertical: "bottom", horizontal: "right" }}
      >
        <Stack spacing={2} sx={{ p: 2, width: 240 }}>
          <TextField
            label="Center (MHz)"
            type="number"
            size="small"
            defaultValue={sdr.tuning.centerFreqHz / 1e6}
            onChange={(e) => void sdr.setTuning({ centerFreqHz: Number(e.target.value) * 1e6 })}
          />
          <TextField
            label="Sample rate (MS/s)"
            type="number"
            size="small"
            defaultValue={sdr.tuning.sampleRateHz / 1e6}
            onChange={(e) => void sdr.setTuning({ sampleRateHz: Number(e.target.value) * 1e6 })}
          />
          <TextField
            label="LNA gain (dB)"
            type="number"
            size="small"
            defaultValue={sdr.tuning.lnaGain}
            onChange={(e) => void sdr.setTuning({ lnaGain: Number(e.target.value) })}
          />
          <TextField
            label="VGA gain (dB)"
            type="number"
            size="small"
            defaultValue={sdr.tuning.vgaGain}
            onChange={(e) => void sdr.setTuning({ vgaGain: Number(e.target.value) })}
          />
          <FormControlLabel
            control={
              <Switch
                checked={sdr.tuning.ampEnable}
                onChange={(e) => void sdr.setTuning({ ampEnable: e.target.checked })}
              />
            }
            label="Amp"
          />
        </Stack>
      </Popover>
    </Box>
  );
}
```

- [ ] **Step 4: Wire into the app shell, provider tree, and proxy**

In `web/src/components/AppShell.tsx`, add the import after the `ConnectionDot` import (line 26):

```tsx
import HackRfConnect from "./HackRfConnect";
```

And render it in the `Toolbar`, immediately before `<ConnectionDot />` (line 78):

```tsx
          <HackRfConnect />
          <ConnectionDot />
```

In `web/src/main.tsx`, wrap `<App />` with `SdrProvider` (add import and nesting inside `LiveProvider`):

```tsx
import { SdrProvider } from "./sdr/SdrProvider";
// ...
    <ColorModeProvider>
      <LiveProvider>
        <SdrProvider>
          <App />
        </SdrProvider>
      </LiveProvider>
    </ColorModeProvider>
```

In `web/vite.config.ts`, add the `/ingest` websocket proxy inside `server.proxy`:

```ts
      "/ingest": { target: "http://localhost:5285", changeOrigin: true, ws: true },
```

- [ ] **Step 5: Run the component test + full frontend build**

Run: `cd web && npm test -- HackRfConnect`
Expected: PASS (2 tests).

Run: `cd web && npm run build`
Expected: `tsc -b` clean + Vite build succeeds (no type errors from the new files or the wiring).

- [ ] **Step 6: Manual integration verification ("render it and look at it")**

Start the backend (`dotnet run --project src/SignalAtlas.Api`) and `cd web && npm run dev`. Open http://localhost:5173. Confirm:
- The top navbar shows a **"Connect HackRF"** button.
- Clicking it opens the **browser WebUSB device chooser** (permission prompt). With a real HackRF One, selecting it flips the chip to `HackRF <serial>` and the **Live Spectrum** view begins updating from streamed IQ (spectrumFrame events).
- With no device, the chooser shows empty / Cancel produces the error tooltip — no crash.

- [ ] **Step 7: Commit**

```bash
git add web/src/components/HackRfConnect.tsx web/src/components/HackRfConnect.test.tsx web/src/components/AppShell.tsx web/src/main.tsx web/vite.config.ts
git commit -m "feat(web): navbar HackRF connect control + app wiring"
```

---

## Self-Review

**Spec coverage:**
- §4.1 RX-only driver → Task 4 (TX stripped, no-TX guard test). ✓
- §4.2 iqSocket config + binary + backpressure → Task 5. ✓
- §4.3 SdrProvider state → Task 6. ✓
- §4.4 navbar HackRfConnect → Task 7. ✓
- §4.5 Vite `/ingest` proxy → Task 7 step 4. ✓
- §5.1 BrowserUploadSampleSource → Task 1. ✓
- §5.2 WS endpoint + Program.cs wiring → Task 2. ✓
- §6 wire protocol (config JSON + binary int8) → Tasks 2, 5, 6. ✓
- §7 invariants: receive-only both ends (Tasks 1, 4 guard tests), egress (no raw-IQ persistence — Task 1 comment; no forbidden field names introduced), auth (`/ingest/iq` outside `/api/v1` — Task 2). ✓
- §8 tests: backend unit + integration (Tasks 1–2), frontend unit + component (Tasks 4–7). ✓
- §9 scope boundary: decode remains deferred — no decode work in any task (correct; the pipeline already runs decode-free). ✓

**Placeholder scan:** No TBD/TODO; every code step contains full code. Two inline "Note" corrections (RemoveAll using-directive; `useCallback` import casing) are flagged explicitly so the implementer fixes them rather than guessing.

**Type consistency:** `HackRfDevice`/`HackRfDeviceInfo` (Task 4) consumed unchanged in Task 6; `IqSocket.sendIq/sendConfig/drops` (Task 5) match SdrProvider usage; `sdrReducer`/`INITIAL_SDR_STATE`/`DEFAULT_TUNING`/`SdrTuning` exported from SdrProvider and imported by both `sdrReducer.test.ts` and `HackRfConnect.test.tsx`; `BrowserUploadSampleSource(capacity, samplesPerBlock)` + `Enqueue(bytes, center, rate)` + `Complete()` identical across Task 1 impl and Task 2 consumption.

## Known follow-ups (out of scope, tracked)
- `navigator.usb.getDevices()` silent reconnect on mount (driver already has `adopt()`; wiring deferred).
- Device *determination* / decode still requires the deferred IQ→bits demodulators (unchanged by this work).
- Optional MessagePack/token auth on `/ingest/iq` if the single-operator posture is ever hardened.
