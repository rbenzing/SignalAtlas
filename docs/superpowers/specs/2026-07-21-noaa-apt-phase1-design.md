# NOAA APT Weather-Satellite Subsystem — Phase 1 Design

**Status:** Approved (brainstorming) — ready for implementation planning.
**Date:** 2026-07-21
**Scope:** Phase 1 only. Georeferenced map placement is deferred to a separate Phase 2 design.

## 1. Goal

Receive a live NOAA APT weather-satellite pass (137 MHz), FM+APT-demodulate it into the
greyscale image, surface the satellite as a `Device` with decoded info, and display the decoded
image inline in that device's detail drawer. Fully offline, receive-only, deterministic.

## 2. Why this is a new subsystem (not an extension of the frame decoders)

The existing decode path is `IDemodulator` → frame bytes → `IDecoderRegistry` → `IProtocolDecoder`
→ `IDeviceResolver`. That path emits **discrete, CRC-validated identity frames per IQ block**
(ADS-B, RDS). APT is fundamentally different: one image is built **continuously over a ~15-minute
pass** at 2 lines/sec — there are no per-block frames and no CRC. It also requires an **FM audio
discriminator**, which does not exist anywhere in the codebase today (`FmRdsDecoder` operates on
already-demodulated RDS group bytes, not IQ; the IQ→bits front-end is deferred for everything
except ADS-B). So APT gets its own parallel, stateful "pass decoder" seam that runs alongside — not
through — the frame path. Neither path touches the other.

## 3. Prime-invariant carve-out (must be documented in `.claude/CLAUDE.md` #3)

Invariant #3 is "metadata, not content — never parse/persist encrypted payload or personal content."
A weather image is transmission **payload/content**, so Phase 1 introduces a **documented, minimal
exception**, to be added to invariant #3:

> **Exception:** public, unencrypted, non-personal broadcast imagery (NOAA APT weather satellites,
> 137 MHz) MAY be decoded and held **in-memory at the edge for local display only**. It is **never
> persisted** and the M13 `EgressGuard` **still blocks it from leaving the edge**.

Consequences that the implementation must honor:
- Image bytes live only in a bounded in-memory store; nothing is written to disk or DB.
- The device **metadata** (satellite, frequency, line count, pass start, evidence) persists through
  the normal `Device` store like any other device.
- `EgressGuard` must continue to block the raw image from any egress payload (M13). The image is
  display-only, served over the local gated API.

## 4. Components

### New (backend)
| Component | Project | Kind | Responsibility |
|---|---|---|---|
| `FmDiscriminator` | `SignalAtlas.Processing` | pure DSP (one-sample phase carry) | IQ block (2 MS/s) → FM-discriminate (phase difference) → low-pass + decimate by an **exact integer factor** to a ~10 kHz audio rate (e.g. 2 MS/s ÷ 200 = 10 kHz) so decimation is deterministic and slot-aligned. Deterministic. |
| `AptDecoder` | `SignalAtlas.Decode` | stateful, per-stream | audio → envelope-detect the 2400 Hz AM subcarrier → detect APT sync-A → append greyscale rows into a growing image; identify the bird from center frequency; emit a `SatellitePass` once sync locks and ≥ `MinLinesToEmit` lines are in (named constant, default 4 lines ≈ 2 s). |
| `AptImageStore` | `SignalAtlas.Persistence` | thread-safe singleton, bounded LRU | `byte[]` PNG keyed by device id; capacity-capped (~16 images) so a long session can't grow unbounded. |

### The seam
```csharp
public interface ISatelliteImageDecoder
{
    bool AppliesTo(long centerFreqHz);            // true inside the 137 MHz APT band
    SatellitePass? Accept(IqBlock block, FeatureVector f, DateTimeOffset t);
}

public sealed record SatellitePass(Device Device, byte[] PngImage, int Lines, double SyncQuality);
```

### Satellite frequency → name (deterministic table)
| Center frequency | Satellite |
|---|---|
| 137.100 MHz | NOAA-19 |
| 137.620 MHz | NOAA-15 |
| 137.9125 MHz | NOAA-18 |

### Wiring (existing files, additive only)
- `IngestionPipeline` — per `IqBlock`, if `ISatelliteImageDecoder.AppliesTo(centerFreqHz)`, feed the
  block to the decoder (parallel to the ADS-B demod branch). On a non-null `SatellitePass`:
  `_aptImages.Put(pass.Device.Id, pass.PngImage)`, `_devices.Upsert(pass.Device)`,
  `_notifier.DeviceDetermined(pass.Device)`.
- `Program.cs` / `IqIngressEndpoint.cs` — register `FmDiscriminator`, `ISatelliteImageDecoder`
  (**`AddTransient`** — landmine #10, carries pass state), `AptImageStore` (singleton); thread the
  decoder + store into both pipeline build sites (mirroring the ADS-B demod resolution).
- `web/src/sdr/bandPresets.ts` — new **NOAA APT 137 MHz satellite** band with the three bird
  channels at 2 MS/s. Distinct from the existing 162 MHz **NOAA Weather Radio** voice band (unrelated).

## 5. Data flow (Phase 1)

1. Operator selects the NOAA APT 137 MHz band → `tune()` → HackRF streams 137.100 MHz @ 2 MS/s IQ
   over `/ingest/iq` (the connect-idle flow is the entry point).
2. Pipeline, per `IqBlock`: `AppliesTo(centerFreqHz)` true → `Accept(block, …)`.
3. `FmDiscriminator(block)` → decimated audio → `AptDecoder` envelope-detects the 2400 Hz subcarrier,
   sync-locks lines, appends greyscale rows, tracks line count + sync quality.
4. Sync locked and ≥ `MinLinesToEmit` lines → returns `SatellitePass(Device, PngImage, Lines, SyncQuality)`.
5. Pipeline stores the PNG, upserts the device, pushes `device.determined`. Subsequent blocks grow the
   same pass → idempotent re-upsert with a refreshed line count/image (stable device id — exactly like
   aircraft re-sightings merge).
6. Frontend: the satellite appears in the Devices list; clicking it opens the detail drawer, which
   fetches and renders the image.

## 6. Device shape (reuses the existing `Device` record — no schema change)

- `Id` = `DeterministicGuid.From($"NOAA-APT:{satellite}:{passStartTicks}")` — stable per pass so blocks
  merge; deterministic (P5). `passStart` comes from the injected clock / block time (no wall clock).
- `DeviceType = "Satellite"`, `Protocol = "NOAA-APT"`, `PrimaryIdentifier = satellite` (e.g. `NOAA-19`).
- `Identifiers = { satellite, frequencyMhz, lines, passStart }`.
- `Evidence` (non-empty, P4/P6): `[("satellite_freq","137.100 MHz",1.0), ("apt_sync","locked",syncQuality),
  ("subcarrier","2400 Hz",…)]` — bounded, deterministic, never the pixel content.
- `Confidence` = sync quality (0..1).
- `Latitude/Longitude/AltitudeFt = null` (georef is Phase 2).

## 7. API

- **`GET /api/v1/devices/{id}/image`** → `200 image/png` (bytes from `AptImageStore`) or `404` when
  no image is held (evicted/pending). Inside `/api/v1` → auth-gated (invariant #5) and audit-logged
  (`read:device-image`). The image never enters the JSON list payload.

## 8. Frontend (Devices page detail drawer)

- The satellite renders in the Devices list with a `NOAA-APT` protocol pill / `Satellite` type.
- For `protocol === "NOAA-APT"`, the detail drawer adds: decoded-info rows (satellite, frequency,
  lines, pass start, sync-quality = confidence), the existing `EvidenceList`, and the **image inline**.
- Image load: new `getDeviceImage(id): Promise<Blob | null>` — fetches the gated PNG as a blob →
  `URL.createObjectURL` → `<img>`, revoked on unmount; `404 → null →` "image pending". Blob fetch
  (not a raw `<img src>`) keeps it on the authenticated API path and handles 404 cleanly.
- No change to the `Device` TypeScript type — satellite fields ride in the existing `identifiers` map.

## 9. Testing (TDD — mirrors the ADS-B synthetic-modulator approach)

- **`AptModulator`** (test helper, the decoder's inverse): known small greyscale image → APT sync +
  2400 Hz AM line words → FM-modulated IQ @ 2 MS/s.
- **`FmDiscriminatorTests`**: a known FM tone → recovered instantaneous frequency within tolerance.
- **`AptDecoderTests`**: synthetic APT audio → sync locks; a gradient image round-trips (decoded
  correlates with source within tolerance); satellite identified from center frequency; non-empty
  evidence; no-sync input → no device.
- **Pipeline integration** (extend `IngestionPipelineDecodeTests`): synthetic APT IQ @ 137.1 MHz →
  `NOAA-APT` device in the repo + PNG in `AptImageStore` + endpoint returns it; a 915 MHz block yields
  **no** satellite device (`AppliesTo` gating).
- **Contract**: `GET /devices/{id}/image` sits inside `/api/v1` → the existing enumerated-endpoints
  auth test covers it (invariant #5).
- **Determinism** (P5): same IQ → byte-identical image.

## 10. Error handling / bounds

- No sync lock / weak pass → no device emitted (deterministic no-op, like ADS-B with no valid frames).
- Non-137 band → `AppliesTo` false → decoder never invoked.
- `AptDecoder` is `AddTransient` (per-stream pass state — landmine #10); `AptImageStore` is a
  thread-safe singleton bounded LRU (~16 images).
- Decoded image **height is capped** at `MaxImageLines` (named constant, default ~3000 lines — a full
  ~15-min pass is ~1800) so a stuck/endless stream can't grow unbounded; the decoder stops appending
  past the cap but keeps the device.
- Endpoint returns `404` when the image is evicted/pending → drawer shows "image pending."

## 11. Out of scope (Phase 2, separate design)

- **Georeferenced placement** of the image on the RF Map. Requires orbital elements (TLE) + pass
  timing + projection; TLEs are online/stale-prone and conflict with offline-first — that trade-off is
  its own design decision, deliberately excluded here.
- Channel-B / sensor-type identification (visible vs IR wedge decoding).
- Any persistence of image content (remains in-memory only in Phase 1).

## 12. Global constraints (carry into the plan)

- **Receive-only (L1):** no transmit path. DSP reads only the magnitude/phase envelope.
- **Deterministic core (P5):** no `DateTime.Now`/`Guid.NewGuid()`/unseeded RNG in `src/`; use `IClock`,
  `DeterministicGuid.From`, seeded RNG. Same IQ → identical image + device id.
- **Explainable-only (P4/P6):** the satellite device carries non-empty, bounded evidence.
- **Invariant #3 carve-out (§3):** image content is in-memory only, never persisted, never egressed;
  `EgressGuard` stays in force.
- **Auth-gated `/api/v1` (invariant #5):** the image endpoint is inside the gated group.
- **Stateful decoders are `AddTransient` (landmine #10);** the image store is a bounded thread-safe
  singleton.
