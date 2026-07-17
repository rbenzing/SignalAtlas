# Design — Live-only when connected + visible scanning

**Date:** 2026-07-17
**Status:** Approved
**Topic:** `live-only-and-visible-scanning`

## Problem
With a HackRF connected and streaming over WebUSB, the UI still shows seeded **demo**
content (Alerts, Devices, the RF Map's Boston markers), and the **Live Spectrum waterfall**
shows only "a scrolling blue field with a center line" — no visible signals. Root causes:

1. The in-memory repos are seeded at startup and never suppressed, so demo data persists
   regardless of a connected device. The backend has no notion of "a device is streaming".
2. `SignalProcessor.ComputePsd` does not suppress the **DC bin**, so the DC-offset spike sits at
   the center bin of every frame; and the frontend `powerRange` uses **raw global min/max**, so
   that one spike (or one strong emitter) sets `max` and compresses all real signal into the
   flat blue floor — real frequencies become invisible.

## Decisions (from brainstorming)
- **Trigger:** the first `/ingest/iq` stream = "device connected/streaming".
- **On disconnect:** stay empty / keep last live (demo returns only on API restart) — so the
  seed clear can be a **one-time latched clear**, no reversible machinery.
- **Empty views are correct** while streaming: Devices empty (device determination is the
  deferred decode seam), RF Map loses the seeded markers (live emitters have no single-sensor
  location yet). Signals/waterfall/occupancy/coverage show live.
- **Backend PSD stays untouched** (deterministic core + its tests); visibility is fixed in the
  frontend rendering only.

## Part 1 — Backend: clear demo seed on first stream
- New `IDemoSeedStore { void ClearDemoSeed(); }` (Persistence). Implemented by the in-memory
  repos + spectrum buffer; each clears its own list/dict under its existing lock.
- New singleton `ILiveSession` with `OnDeviceStreamStarted()` — latched via `Interlocked` so it
  clears **all** registered `IDemoSeedStore`s exactly once per process. Reconnects don't re-clear
  (live data survives a disconnect/reconnect).
- DI: `ILiveSession` registered always; the in-memory repos are additionally registered as
  `IDemoSeedStore` in the in-memory persistence branch. In **DB mode** nothing implements
  `IDemoSeedStore` → the clear is a no-op (never wipes a real database).
- `IqIngressEndpoint`: resolve `ILiveSession` and call `OnDeviceStreamStarted()` when a stream
  starts (after the config frame is accepted).

## Part 2 — Frontend: robust waterfall scaling + DC handling
- Replace `powerRange(frames)` raw min/max with a **robust range**: percentiles (≈5th/99th) over
  bins **excluding the DC center bin (index N/2, ±1 guard)**, computed from the newest frame(s)
  and **EMA-smoothed** across renders for stability and cheapness (avoid O(all-frames) sorts each
  render). Fallback to `{min:-100, max:0}` when degenerate.
- `Waterfall` render: de-emphasize the DC center bin (±1) — paint it from neighbor average so the
  bright center line disappears.
- The occupancy chart and power-scale legend consume the same robust range.
- All pure functions in `web/src/lib/spectrum.ts`, unit-tested (a single outlier spike must not
  collapse the visible range; DC bin excluded).

## Part 3 — Frontend: visible "scanning" indicator
- Track live `spectrumFrame` arrival rate (fps) — a small counter in the live layer.
- `HackRfConnect` navbar chip, while streaming, shows **"Scanning · N fps"** + center frequency
  (and existing drops). 0 fps ⇒ visible signal that IQ isn't reaching the backend.

## Testing
- Backend: `ILiveSession` clears once + is idempotent on repeat calls; each in-memory repo's
  `ClearDemoSeed` empties it; integration — after a stream starts, `/devices` and `/emitters`
  return empty and `/signals` returns live.
- Frontend: robust-range unit tests (outlier spike ignored, DC excluded, EMA smoothing);
  `HackRfConnect` shows the scanning/fps readout when streaming.
- End-to-end: drive the running app in a browser with a synthetic stream carrying a DC spike;
  confirm the waterfall shows visible signal content (not just blue+line) and Devices/Alerts are
  empty on connect.

## Non-goals
- Real device geolocation / decode (deferred seams) — Devices/Map stay empty by design.
- Reversible demo restore on disconnect (explicitly chosen against; restart restores demo).
