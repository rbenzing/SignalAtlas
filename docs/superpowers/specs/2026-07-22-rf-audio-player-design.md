# RF Audio Player (server-side demod) — Design

**Status:** Approved (fully-autonomous mandate — user reviews merged result).
**Date:** 2026-07-22
**Goal:** Replace the Coverage/"Tune" card on the Live Spectrum ("RF Wave") page with a streaming audio player that lets the operator LISTEN to the currently-tuned signal (FM / AM), demodulated **server-side** from the live IQ.

## 1. Why server-side (chosen)

Demodulation happens in the backend, which already ingests IQ from every source (WebUSB `/ingest/iq`, file, synthetic, native HackRF) through `IngestionPipeline`. The browser just plays an audio stream. This works for ALL sources (client-side would only work in WebUSB mode), keeps DSP in one tested place (C#), and reuses the `FmDiscriminator` already built for NOAA APT. Modes for Phase 1: **WBFM** (wide FM, broadcast), **NBFM** (narrow FM, ham/marine/airband-FM), **AM** (airband, shortwave). SSB (USB/LSB) is a deferred follow-on.

## 2. Architecture

```
IQ blocks (IngestionPipeline)  ──tap──►  IAudioDemodulator (per audio session, stateful)
                                             │  FM: FmDiscriminator → de-emphasis → decimate to 24 kHz
                                             │  AM: |IQ| envelope → DC-block → decimate to 24 kHz
                                             ▼
                                     AudioHub (per-connection PCM16 mono ring)
                                             ▼
                          WebSocket  /audio  (binary PCM16LE @ 24 kHz, mono)  ◄── JSON config {mode, enabled}
                                             ▼
                          Browser: AudioStreamPlayer (Web Audio AudioContext + AudioWorklet)
                                             ▼
                          LiveSpectrum page: <AudioPlayer/> replaces the Coverage card
```

- **Tap point:** the audio path must see the same IQ blocks the pipeline processes. Add an optional `IAudioSink? audio` to `IngestionPipeline`; in the per-block loop, after DSP, call `audio?.Accept(block)`. `IAudioSink` is a singleton fan-out that holds the active demod + the set of connected audio WebSockets; when no client is connected it is a no-op (zero cost).
- **Demod is deterministic pure DSP** (reuses `FmDiscriminator`); output is PCM16 mono at a fixed **24 kHz** (integer-decimated from the block sample rate; require `SampleRateHz % 24000 == 0`? No — 2e6/24000 is non-integer. Use **decimate to the nearest integer factor then linear-resample to 24 kHz** deterministically, OR pick audio rate = 25000 (2e6/80=25000, integer) and stream at 25 kHz. **Decision: 25 kHz audio, integer decimation** — Web Audio resamples to the device rate transparently.). Correction: use **AudioRateHz = 25_000** (2 MS/s ÷ 80).
- **Receive-only:** audio is a *read* of the RF; no transmit. L1 intact.
- **Not content-persistence:** audio is streamed live and never stored (consistent with the raw-IQ-never-persisted posture; audio is a transient rendering, like the waterfall).

## 3. Backend components

| Component | Project | Kind | Responsibility |
|---|---|---|---|
| `IAudioDemodulator` + `FmAudioDemod`/`AmAudioDemod` | `SignalAtlas.Processing` | stateful per session, pure DSP | IQ block → PCM16 mono @ 25 kHz for the selected mode. FM reuses `FmDiscriminator` (+ simple de-emphasis 1-pole); AM = magnitude envelope + DC block. |
| `IAudioSink` / `AudioHub` | `SignalAtlas.Api` | singleton | Holds the active mode + connected audio sockets; `Accept(IqBlock)` demods and fans PCM out to each socket (drop-oldest backpressure). No-op when no client. |
| `/audio` WebSocket endpoint | `SignalAtlas.Api` | ungated (like `/hub/live`, `/ingest/iq`) | JSON `config` frame `{mode:"wbfm"|"nbfm"|"am", enabled:bool}` in; binary PCM16LE frames out. |

- `IngestionPipeline` gains `IAudioSink? audio = null` (appended optional param) + one `audio?.Accept(block)` call per block. Both build sites (`PipelineHostedService`, `IqIngressEndpoint`) pass `sp.GetService<IAudioSink>()`.
- DI: `AddSingleton<IAudioSink, AudioHub>()`; demodulators constructed inside the hub per active mode.
- `/audio` is mapped OUTSIDE `/api/v1` (like `/hub/live`), so the auth-gate contract test is unaffected; documented in OPERATIONS as another ungated loopback endpoint.

## 4. Frontend components

- `web/src/audio/AudioStreamPlayer.ts` — opens the `/audio` WebSocket, sends the config frame, feeds incoming PCM16 into a Web Audio `AudioContext` via an `AudioWorkletNode` (ring-buffer worklet) for glitch-resistant playback; exposes `play(mode)`, `stop()`, `setVolume()`, and a level readout.
- `web/src/components/AudioPlayer.tsx` — the card UI: mode selector (WBFM / NBFM / AM), Play/Stop, volume slider, a simple level meter, and the current tuned-frequency label (from `useSdr().activeFreqHz`). Shows "Tune a frequency to listen" when `activeFreqHz` is null.
- `web/src/views/LiveSpectrum.tsx` — replace the `Coverage` `ChartCard` (the md:4 right column) with `<AudioPlayer/>`. (Coverage moves nowhere else in Phase 1 — the user asked to replace it; the coverage data is still available on the Dashboard.)

## 5. Constraints / invariants

- Receive-only (L1); deterministic DSP core (P5) — the demods are pure, no wall clock; the hub/socket layer is API-side (per-request/streaming, allowed non-deterministic).
- Audio never persisted; no content leaves as a stored artifact (transient stream only).
- Backpressure: drop-oldest on the PCM ring per socket (like the IQ ingress), counted.
- No new backend NuGet deps. Frontend: Web Audio API + AudioWorklet only (no new npm dep).
- `/audio` ungated + loopback-only posture (same as `/ingest/iq`), documented.

## 6. Testing

- `FmAudioDemod`/`AmAudioDemod`: feed a synthetic modulated IQ tone (a known audio tone FM/AM-modulated onto a carrier) → assert the recovered PCM contains that audio tone (FFT peak at the right bin) within tolerance; determinism (same IQ → same PCM).
- `AudioHub`: `Accept` with no clients is a no-op; with a fake sink, mode switch produces the right demod; drop-oldest bound holds.
- `/audio` endpoint: integration test — connect, send config, feed IQ through the pipeline, assert PCM frames arrive; ungated by design (not in the `/api/v1` enumeration).
- Frontend: `AudioStreamPlayer` unit test with a fake WebSocket + stubbed AudioContext (assert config sent, PCM routed); `AudioPlayer` view test (mode select → play calls player; null activeFreqHz → disabled state).

## 7. Out of scope (Phase 1)

- SSB (USB/LSB) demod; squelch; recording/export; per-channel audio (only the currently-tuned center is demodulated).
