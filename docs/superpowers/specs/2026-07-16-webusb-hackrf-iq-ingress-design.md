# Design — Browser WebUSB HackRF One → Backend IQ Ingress

**Date:** 2026-07-16
**Status:** Approved (design), pending implementation plan
**Topic:** `webusb-hackrf-iq-ingress`

## 1. Problem & goal

Signal Atlas today has **no browser-side SDR access**. The frontend is a pure REST +
SignalR client; the HackRF is owned by the .NET backend (`SoapyHackRfDevice` / `ISampleSource`),
and that hardware path is a documented *deferred* seam ("needs the device + `.iq` captures").
There is no way for a user running the web app to point the platform at a HackRF One that is
plugged into **their own machine**.

**Goal:** Let the browser own a locally-attached HackRF One over **WebUSB** — with an easy
device picker in the **top navbar**, a real browser permission prompt, and connect/disconnect —
then stream received IQ up to the .NET backend so the *existing* ingestion pipeline classifies
protocols and drives the live UI (waterfall / occupancy / coverage). The browser is a **pure
USB tether**; all intelligence and every invariant stay in the backend.

### Non-goals
- **Device *determination* / decode is NOT in scope.** The IQ→bits demodulators are a separate
  deferred backend seam; WebUSB delivers real IQ but does not unlock decode. Live mode will
  classify protocols and correlate RF-only, exactly as documented today.
- **No transmit.** Ever. (Prime invariant #1.)
- **No in-browser DSP.** The backend computes the spectrum; the browser does not duplicate it.
- Multi-client / multi-sensor fusion is out of scope (single-node, single active device).

## 2. Decisions (locked during brainstorming)

1. **Radio ownership:** Browser WebUSB → stream IQ to backend. Backend keeps all intelligence.
2. **Transport:** Dedicated **binary WebSocket** endpoint (`/ingest/iq`), a JSON text frame for
   config/tuning + binary frames for raw int8 IQ.
3. **Pipeline shape:** **Per-connection** `IngestionPipeline.Run` fed by a channel-backed
   `ISampleSource`; backend computes the spectrum; browser does no DSP.

## 3. Architecture & data flow

```
HackRF One ──USB──▶ Browser (WebUSB, RX-only driver)
                        │  int8 interleaved I/Q, read loop (bulk IN 16384 B)
                        ▼
              Binary WebSocket  /ingest/iq   (localhost, un-gated)
                        │  text config frame + binary IQ frames
                        ▼
   Backend WS handler ─▶ BrowserUploadSampleSource : ISampleSource
                        │  int8 → /128f → IqBlock(center, rate, I[], Q[])
                        ▼
              IngestionPipeline.Run   (existing, untouched)
              PSD → SpectrumFrame → notifier.SpectrumFrame()
              features → Classify → signalCreated → correlate → …
                        │
                        ▼
        SignalR /hub/live ── spectrumFrame / signalCreated ──▶ LiveProvider → LiveSpectrum view
```

The pipeline and all downstream SignalR events are reused verbatim. The only greenfield surface
is: the browser WebUSB driver, the WS transport on both ends, and the channel-backed source.

## 4. Frontend design (`web/src/sdr/` + navbar)

Stack context: React 18 + TS, MUI + Emotion (`sx`), React Context + hooks (no Redux/Zustand),
`@microsoft/signalr` for the live hub, Vite dev server on 5173 proxying to 5285.

### 4.1 `sdr/hackrf.ts` — RX-only HackRF WebUSB driver
Adapted from signal-weaver's `src/lib/hackrf-usb.ts`, **with all transmit code removed**.

- IDs: vendor `0x1d50`, product `[0x6089 (HackRF One), 0x604b (Jawbreaker)]`.
- `requestDevice()` → `navigator.usb.requestDevice({ filters })` (must run on a user gesture).
  Error handling: absent `navigator.usb` → Chrome/Edge + Zadig/WinUSB guidance;
  `NotFoundError` → "no device / install WinUSB"; `SecurityError` → reopen in a top-level tab.
- `open/claim`: `open()` → `selectConfiguration(1)` if null → `claimInterface(iface0)` → find
  the bulk-IN endpoint (fallback endpoint 1).
- Control-transfer helpers: `{ requestType:'vendor', recipient:'device' }`.
- Vendor requests retained (RX only): `SET_TRANSCEIVER_MODE(1)`, `SAMPLE_RATE_SET(6)`,
  `BASEBAND_FILTER_BANDWIDTH_SET(7)`, `BOARD_ID_READ(14)`, `VERSION_STRING_READ(15)`,
  `SET_FREQ(16)`, `AMP_ENABLE(17)`, `BOARD_PARTID_SERIALNO_READ(18)`, `SET_LNA_GAIN(19)`,
  `SET_VGA_GAIN(20)`.
- **Removed:** `SET_TXVGA_GAIN(21)`, `startTx()`, `setTxVgaGain()`, and `TransceiverMode.TX`.
  `TransceiverMode` is limited to `OFF(0) | RX(1)`.
- `setFrequency(hz)`: 8-byte payload `uint32 MHz` + `uint32 Hz-remainder`, little-endian.
- `setSampleRate(hz)`: 8-byte `uint32 rate` + `uint32 divider(=1)`.
- `setBasebandFilter(bw)`: value = `bw & 0xffff`, index = `(bw>>16) & 0xffff`, no data;
  round down to a valid MAX2837 value.
- Gains via control-IN, value in the index field: LNA (8 dB steps, 0–40), VGA (2 dB steps, 0–62),
  `AMP_ENABLE` control-OUT value 1/0.
- `startRx(onData)`: transceiver mode RX, then a read loop of `transferIn(bulkIn, 16384)`
  delivering **`Int8Array`** (use `byteOffset`/`byteLength`). On `stall`/`babble` →
  `clearHalt` + short backoff; on error → stop + `onEnd(error)`.
- `stop()`: clear `onEnd`, set transceiver mode OFF. `disconnect()`: stop → release → close.

### 4.2 `sdr/iqSocket.ts` — WebSocket IQ client
- Opens `ws(s)://<host>/ingest/iq`. On open, sends the JSON **config** text frame.
- Read-loop callback → send the `Int8Array` block as a **binary** frame.
- **Backpressure:** if `ws.bufferedAmount` exceeds a threshold (e.g. a few blocks), drop the
  oldest/incoming block rather than queueing unboundedly (mirrors backend `BoundedSampleSource`
  drop-oldest). Count drops for a UI indicator.
- Auto-reconnect with backoff; resend config on reconnect.

### 4.3 `sdr/SdrProvider.tsx` — device state context
- Same shape as `LiveProvider`. State machine: `idle → requesting → connecting → streaming → error`.
- Holds `serial`, `firmware`, and `tuning { centerFreqHz, sampleRateHz, lnaGain, vgaGain, ampEnable }`.
- Exposes `connect()`, `disconnect()`, `setTuning(partial)`.
- On mount: `navigator.usb.getDevices()` to detect a previously-authorized HackRF and offer
  one-click reconnect (no chooser needed the second time).
- Wires `hackrf.startRx` → `iqSocket.send`. `setTuning` applies to the device (setFrequency/…)
  and re-sends the config frame.

### 4.4 `components/HackRfConnect.tsx` — navbar control
- Rendered in `AppShell` top bar, beside `ConnectionDot`.
- **Disconnected:** "Connect HackRF" button → triggers the browser permission chooser.
- **Connected:** serial + a streaming/drops indicator + a settings popover (center freq,
  sample rate, LNA, VGA, amp toggle) + "Disconnect".
- Consumes `useSdr()`.

### 4.5 Vite proxy
Add to `web/vite.config.ts` `server.proxy`:
`"/ingest": { target: "http://localhost:5285", changeOrigin: true, ws: true }`.

## 5. Backend design

### 5.1 `SignalAtlas.Collector/BrowserUploadSampleSource.cs` — `: ISampleSource`
- Backed by a **bounded** `Channel<IqBlock>` with **drop-oldest** semantics (mirrors
  `BoundedSampleSource`).
- `Enqueue(ReadOnlyMemory<byte> int8Iq, long centerFreqHz, int sampleRateHz, int samplesPerBlock)`:
  convert interleaved signed-8-bit I/Q → `IqBlock` via `i = (sbyte)b / 128f` (reuse
  `FileSampleSource`'s conversion), chunk by `samplesPerBlock`, write to the channel.
- `Blocks()` yields from the `ChannelReader` until `Complete()` is called.
- **Naming constraint:** no member name may contain `tx`, `send`, or `transmit` (reflection
  test on `ISampleSource`; keep the spirit on the concrete type too). Use `Enqueue` / `Complete`.
- Raw IQ is consumed into `IqBlock`s and **never persisted or forwarded** (egress discipline).

### 5.2 WebSocket endpoint `/ingest/iq`
- Add `app.UseWebSockets()` and `app.Map("/ingest/iq", handler)` in `Program.cs`, **outside the
  `/api/v1` group** — a deliberate un-gated inbound surface, consistent with `/hub/live`
  (invariant #5 explicitly exempts hub/live-style transports; no `/api/v1` route changes).
- Handler:
  1. Accept the socket; read the first **text** frame → parse JSON config.
  2. Create a `BrowserUploadSampleSource`; open a DI scope and construct/resolve the pipeline
     collaborators exactly as `PipelineHostedService` does (`ISignalProcessor`, `IClassifier`,
     `ILiveNotifier`, `ISpectrumBuffer`, correlation/anomaly/persistence).
  3. Start `IngestionPipeline.Run(source, ct)` on a background task.
  4. Loop: **binary** frame → `source.Enqueue(bytes, cfg.CenterFreqHz, cfg.SampleRateHz,
     cfg.SamplesPerBlock)`. A re-sent **text** config frame updates the current tuning.
  5. On close/error/cancel → `source.Complete()`, await/cancel the run, dispose the scope.
- Availability: endpoint is always mapped; it is only active while a browser streams. (No new
  config flag required; `Ingestion__Enabled` continues to gate only the file/synthetic startup pass.)

### 5.3 Reused, unchanged
`IqBlock`, `IngestionPipeline`, `ISignalProcessor` (PSD + features), `IClassifier`,
`SpectrumFrame`, `SignalRLiveNotifier` (`spectrumFrame`, `signalCreated`, …), `LiveHub`,
`ISpectrumBuffer`, `GET /api/v1/spectrum/frames`.

## 6. Wire protocol

- **Config (text / JSON):**
  ```json
  { "type": "config", "centerFreqHz": 915000000, "sampleRateHz": 2000000,
    "samplesPerBlock": 8192, "collectorId": "web-hackrf-1" }
  ```
  Sent on open and on every tuning change.
- **IQ (binary):** raw interleaved signed-8-bit `I,Q,I,Q…`, `2 × samplesPerBlock` bytes per
  frame. The backend applies the most recent config.
- **Defaults:** 915 MHz center, 2 MS/s (~4 MB/s over localhost), LNA 16 dB, VGA 20 dB, amp off.
  All adjustable from the navbar popover.

## 7. Invariants & guards

- **Receive-only (#1):** enforced on **both** ends. Browser driver has no TX code path + a
  frontend guard test asserts no `tx|transmit|send` method exists. Backend source implements
  `ISampleSource`, already covered by `ISampleSource_ExposesNoTransmitMember`.
- **Metadata-not-content / egress (#3):** raw IQ → PSD/features → discarded; never persisted
  (matches `ScanCollector` `IqRef = null`) and never enters the Claude egress payload; no new
  field names collide with `EgressGuard`'s forbidden markers.
- **Auth (#5):** unchanged; `/ingest/iq` is an intentional un-gated inbound surface alongside
  `/hub/live`. No `/api/v1` route becomes ungated.
- **Deterministic core (#4):** browser/HTTP/WS is outside the deterministic domain core; the
  backend pipeline it feeds remains deterministic on identical byte input.

## 8. Testing (TDD, RED → GREEN)

**Backend (`SignalAtlas.Tests.Unit` / integration):**
- `BrowserUploadSampleSource` converts int8 (−128..127) → normalized IQ (`/128f`), correct
  I/Q interleaving, correct `samplesPerBlock` chunking.
- Yields blocks until `Complete()`, then terminates cleanly.
- Drop-oldest under a full bounded channel (mirror `BoundedSampleSource` behavior).
- Exposes no `tx|send|transmit` member (explicit assertion on the concrete type).
- WS endpoint integration: connect a test WebSocket, send config + one binary block, assert a
  `spectrumFrame` is emitted via a fake `ILiveNotifier`.

**Frontend (Vitest / Playwright):**
- `hackrf.ts` no-TX guard: no exported method name matches `/tx|transmit|send/i`.
- Config-frame serialization shape + binary frame sizing (`2 × samplesPerBlock`).
- `iqSocket` backpressure: drops when `bufferedAmount` is high; counts drops.
- `HackRfConnect` renders the Connect button and calls `requestDevice` on click with a mocked
  `navigator.usb`; connected state shows serial + Disconnect.

## 9. Scope boundary (explicit)

**Delivered:** navbar Connect with a real browser permission prompt + device chooser (and
one-click reconnect for a previously-authorized device); real IQ streamed to the backend; live
**waterfall / occupancy / coverage**; **protocol classification** (`signalCreated`) + RF-only
correlation.

**Still deferred (unchanged by this work):** device **determination / decode** (IQ→bits
demodulators are a separate backend seam). This is stated so the feature is not mis-sold as
"devices now decode over WebUSB" — it does not.

## 10. Out-of-band note (not part of this feature)

The reported Vite `ECONNREFUSED /api/v1/spectrum/frames` is simply the API not listening on
5285 — launch the **"Full stack: API + Web"** compound (or the "API (live ingestion)" +
"Web dev server") from `.vscode/launch.json`, or `dotnet run --project src/SignalAtlas.Api`.
No code change; unrelated to the ingress design, but the design assumes both processes are up.
