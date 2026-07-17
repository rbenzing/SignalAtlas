# Live ADS-B Demodulator — Stage 1: Aircraft in the Devices List — Design

**Date:** 2026-07-17
**Status:** Approved for planning
**Scope:** Backend only (`src/`). No frontend changes required.
**Stage:** 1 of 2. Stage 2 (CPR position decoding + aircraft on the map) is a separate later spec that depends on this one.

## Summary

Build the missing IQ→frame front-end for ADS-B so live mode determines aircraft. A new
`AdsBDemodulator` turns 1090 MHz IQ into 14-byte Mode S extended-squitter frames; a new decode
stage in `IngestionPipeline` runs it, feeds frames to the existing `AdsBDecoder` →
`DeviceResolver`, and persists/pushes Aircraft devices. Aircraft then appear on the Devices page
(ICAO + callsign). Position/map plotting is explicitly Stage 2.

## Motivation

The live pipeline runs `collect → PSD → features → classify → correlate → anomaly`
([IngestionPipeline.cs](../../src/SignalAtlas.Pipeline/IngestionPipeline.cs)) with **no decode
stage** — decode was deferred behind the `IDemodulator` seam pending a physical HackRF + real
`.iq` captures. As a result live mode classifies protocols but never determines devices, so no
aircraft show up even with planes overhead and a correct 1090 MHz tune. The frame decoder
(`AdsBDecoder`), device resolver (`DeviceResolver`), decoder registry, and the
`ILiveNotifier.DeviceDetermined` live-push hook are all already built and tested — the only
missing pieces are the demodulator and the pipeline wiring.

## What already exists (reused unchanged)

- `IDemodulator` domain seam: `IEnumerable<ReadOnlyMemory<byte>> Demodulate(IqBlock slice, FeatureVector features)`
  ([Decode.cs:50-54](../../src/SignalAtlas.Domain/Decode.cs)).
- `AdsBDecoder` — 14-byte Mode S frame → CRC-24 validate → ICAO (always) + callsign (TC 1-4).
- `DeviceResolver.Resolve(IReadOnlyList<DecodedFrame>)` → one `Device` (Aircraft for ADS-B),
  device id = `DeterministicGuid.From(primary identifier = icao)`.
- `DecoderRegistry.Decode(protocol, frameBytes)` — dispatch by protocol; unknown → no-op.
- `ILiveNotifier.DeviceDetermined(Device)` — wired to SignalR `deviceDetermined` and the frontend hub.
- `InMemoryDeviceRepository` — seeds one demo aircraft, implements `IDemoSeedStore` (already clears
  the seed on first live stream).
- `Devices.tsx` polls `getDevices` every 15 s — live devices appear with no frontend change.

## Approach (chosen: A)

Self-gating per-protocol demodulator + a pipeline decode stage. Reuses the existing
decoder/resolver/registry seams unchanged. Rejected alternatives: (B) a monolithic IQ→Device
component that duplicates the tested CRC/identity logic and abandons the seams; (C) a separate
demod service outside the pipeline, over-engineered for one protocol.

## Component 1 — `AdsBDemodulator`

New file `src/SignalAtlas.Decode/Demodulators/AdsBDemodulator.cs`, implementing `IDemodulator`
with `Protocol => "ADS-B"`.

**Signal facts.** Mode S extended squitter is 1 Mbit/s PPM at 1090 MHz. Each 1 µs bit is two
half-chips: `1` = pulse-then-silence, `0` = silence-then-pulse. A frame is an 8 µs preamble
(pulses at 0, 1.0, 3.5, 4.5 µs) then 112 µs of data → 14 bytes. At the preset 2 MS/s, 1 µs = 2
samples, half-chip = 1 sample, whole frame ≈ 240 samples.

**Algorithm — `Demodulate(IqBlock block, FeatureVector _)`:**
1. **Self-gate:** if 1090 MHz is not within `[CenterFreqHz ± SampleRateHz/2]`, yield nothing.
   Keeps PPM demod off non-1090 blocks, independent of the (unreliable synthetic) classifier.
2. **Envelope:** `mag[n] = I[n]*I[n] + Q[n]*Q[n]` (power; comparisons need no sqrt).
3. **Preamble hunt:** `sps = SampleRateHz / 1_000_000.0` (=2 at the preset). Slide across `mag`;
   at each offset require the four preamble pulse positions to each exceed the intervening low
   positions by a margin. On a hit, advance to the data section.
4. **PPM slice:** for each of 112 bits, `bit = firstHalfSample > secondHalfSample`; pack MSB-first
   into 14 bytes.
5. **Yield** the 14-byte candidate; resume scanning past the frame for further squitters.

CRC is NOT validated here — it stays `AdsBDecoder`'s job, so noise candidates are rejected
downstream by the syndrome check. This keeps demod (physical layer) and decode (frame validation +
identity) cleanly separated.

**Deliberate v1 limits:** frames straddling a block boundary are dropped (stateless per-block
demod; aircraft squitter ~1-2×/s across many blocks, so loss is negligible — cross-block
carry-over is a follow-up). Designed and tested at 2 MS/s; other rates work via `sps` but are
untested.

## Component 2 — pipeline decode stage

`IngestionPipeline` gains four optional constructor dependencies, each null-safe exactly like the
existing optional `_emitters`/`_alerts`/`_spectrum`: `IEnumerable<IDemodulator> demodulators`,
`IDecoderRegistry registry`, `IDeviceResolver resolver`, and the device write path (Component 3).
When any is null the stage is a no-op and the pipeline behaves as it does today.

**Placement:** a new step 4b, after the signal is persisted
([IngestionPipeline.cs:117](../../src/SignalAtlas.Pipeline/IngestionPipeline.cs)) and before
correlation. Per block:

```
decodedFrames = []
foreach demod in _demodulators:
    foreach frameBytes in demod.Demodulate(block, features):
        outcome = _registry.Decode(demod.Protocol, frameBytes)   // CRC + identity
        if outcome.Success: decodedFrames.Add(outcome.Frame)
foreach group in decodedFrames grouped by primary identifier (icao):
    device = _resolver.Resolve(group as list)
    if device is not null:
        _devices.Upsert(device)                // idempotent on deterministic id
        _notifier.DeviceDetermined(device)     // live push (already wired)
```

**Grouping by ICAO** is the only logic added beyond reuse: `DeviceResolver.Resolve` assumes all
frames belong to one device, so the pipeline groups decoded frames by their primary identifier
before resolving — one Aircraft device per aircraft in the block. Because the device id is
`DeterministicGuid.From(icao)`, upserts across blocks/time merge (a callsign frame and a later
frame from the same ICAO converge on one device).

The primary-identifier key is derived the same way `DeviceResolver` picks its primary: for ADS-B,
the `icao` identifier. The pipeline reads `frame.Identifiers["icao"]`; a decoded ADS-B frame always
carries `icao` (the decoder sets it unconditionally), so no null-key group arises for ADS-B.

**Out of scope for v1:** setting `Signal.DeviceId` (signal is created before decode; RF
signal↔device linking is a follow-up, as emitter↔device already is) and any emitter linkage.

## Component 3 — device write path

`IDeviceRepository` is read-only today. Add an idempotent upsert, mirroring
`IEmitterRepository.Upsert`:

```csharp
public interface IDeviceRepository
{
    IReadOnlyList<Device> GetDevices(int limit = 100);
    void Upsert(Device device);   // idempotent on Device.Id
}
```

Implementations:
- **`InMemoryDeviceRepository`** — `Upsert`: lock, replace by `Id` or prepend, newest-first,
  bounded cap (matching the other in-memory repos). Existing `ClearDemoSeed()` already drops the
  seeded demo aircraft on first stream, so a connected HackRF shows live aircraft only. No change
  to seeding/clearing.
- **`EfRepositories`** (Postgres) — `Upsert` via `INSERT … ON CONFLICT (id) DO UPDATE`; JSON
  members (identifiers/evidence) through the existing `ValueConverter`; provider-split PK handled
  by `SignalAtlasDbContext`.
- Any test double gains the method trivially.

**Idempotency is the crux:** the same aircraft re-seen every block upserts to the same row, so the
list shows one accumulating entry per aircraft, not a flood of duplicates.

## Component 4 — DI registration

Register the new demodulator and the decode-stage collaborators where the live pipeline is built
(`Program.cs` / `PipelineHostedService` / `IqIngressEndpoint.BuildPipeline`): `AdsBDemodulator`
as an `IDemodulator`, `DecoderRegistry` (over the registered `IProtocolDecoder`s including the
existing `AdsBDecoder`), `DeviceResolver`, and pass the `IDeviceRepository` write path +
`ILiveNotifier` into the pipeline. Existing non-live pipeline callers that omit these keep the
no-op behaviour.

## Data flow

```
HackRF IQ (1090 MHz) → IqBlock
  → AdsBDemodulator.Demodulate  → 14-byte Mode S candidate frames
  → DecoderRegistry.Decode("ADS-B", bytes) → AdsBDecoder → CRC-24 → DecodedFrame{icao, callsign}
  → group by icao → DeviceResolver.Resolve → Device{Aircraft, icao, callsign, evidence}
  → IDeviceRepository.Upsert  +  ILiveNotifier.DeviceDetermined
  → Devices page (poll every 15s) shows the aircraft
```

## Invariants

- **Receive-only (L1):** demod only reads IQ; no transmit surface added.
- **Metadata, not content (L2/L3):** demod emits raw frames; `AdsBDecoder` extracts only ICAO +
  callsign (cleartext identity); no payload parsed/persisted.
- **Deterministic core (P5):** demod is pure DSP; device ids via `DeterministicGuid`; grouping by
  identifier. No `DateTime.Now`/`Guid.NewGuid()`/unseeded RNG. Same IQ bytes → identical devices.
- **Explainable-only (P4/P6):** every device carries the decoder's evidence (df, crc_pass, icao,
  callsign); `DeviceResolver` already guarantees non-empty evidence.
- **No new `/api/v1` routes.**

## Testing (TDD — synthetic closed-loop is the gate)

1. **Test-only `AdsBModulator`** (in `tests/SignalAtlas.Tests.Unit`, not shipped) — the inverse of
   the demod: a 14-byte frame → preamble + PPM IQ at 2 MS/s. The fixture generator.
2. **`AdsBDemodulator` unit tests:** modulate a known-valid frame → demod → assert exact 14 bytes;
   with seeded deterministic noise still recovers; a pure-noise block yields nothing; an off-1090
   block yields nothing (self-gate); two frames in one block → both recovered; a truncated
   boundary frame → dropped, no crash.
3. **Round-trip through the real decoder:** modulate a frame with a known ICAO/callsign → demod →
   `AdsBDecoder` → assert `DecodeOutcome.Success` with that ICAO/callsign.
4. **`IngestionPipeline` integration test:** blocks containing one synthesized ADS-B frame → assert
   an Aircraft `Device` (correct ICAO) is upserted AND `DeviceDetermined` fired; a no-ADS-B run
   persists zero devices (no regression).
5. **`InMemoryDeviceRepository.Upsert`:** idempotency (same ICAO twice → one entry), newest-first
   bound, thread-safety.

Real `.iq` capture validation is an optional follow-up once a capture is taken off the HackRF.

## Out of scope (Stage 2 / follow-ups)

- CPR airborne-position decoding (TC 9-18) and per-aircraft lat/lon.
- Plotting aircraft on the map.
- Cross-block frame carry-over.
- `Signal.DeviceId` / emitter↔device linkage.
- Instant device hub-merge on the frontend (poll already suffices; the `deviceDetermined` event
  exists if wanted later).
- Short (56-bit / DF11) squitters — only 112-bit DF17/18 extended squitters are demodulated.
