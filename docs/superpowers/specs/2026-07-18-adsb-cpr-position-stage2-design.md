# Live ADS-B — Stage 2: CPR Position → Aircraft on the Map — Design

**Date:** 2026-07-18
**Status:** Approved for planning
**Scope:** Backend (`src/`) + a frontend map layer (`web/`).
**Stage:** 2 of 2. Depends on Stage 1 (demod → `AdsBDecoder` → Aircraft devices), now on main.

## Summary

Decode ADS-B airborne-position frames (TC 9-18) so aircraft get a real lat/lon and plot on the RF
map. `AdsBDecoder` gains position handling but stays pure; a new stateful `CprPositionResolver`
does global (even/odd) CPR decoding; `Device` gains nullable position; the pipeline stamps it; the
map renders a distinct aircraft layer from `/devices`. Zero operator configuration required.

## Motivation

Stage 1 makes aircraft appear as Devices (ICAO + callsign) but with no position — the map plots
located **Emitters**, and `Device` has no lat/lon. Stage 2 closes that: airborne-position squitters
carry a compact (CPR) lat/lon that, decoded, places each aircraft on the map.

## Chosen decisions

- **Global CPR (even/odd pairing), zero-config.** Pair one even + one odd position frame from the
  same ICAO (within ~10 s) → unambiguous worldwide position. No operator location required.
  Rejected: local CPR (needs the operator to configure receiver lat/lon, else a silent empty map);
  both-modes (dump1090-style — most robust but doubles the code/test surface, YAGNI for v1).
- **Approach A — pure decoder + dedicated stateful resolver.** The decoder extracts raw CPR values
  and stays a pure singleton; all pairing state + CPR math live in `CprPositionResolver`.
  Rejected: a stateful decoder (breaks its pure frame-bytes→identifiers contract); reusing Emitters
  for aircraft (correlation groups emitters by frequency, collapsing all 1090 MHz aircraft into one).

## Component 1 — `AdsBDecoder` airborne-position handling (pure)

Extend the decoder (`src/SignalAtlas.Decode/Decoders/AdsBDecoder.cs`) to handle **TC 9-18**
(barometric airborne position) alongside the existing TC 1-4 callsign path. From the 56-bit ME
field it extracts:
- **CPR format bit** F (bit): 0 = even, 1 = odd.
- **17-bit raw CPR latitude** and **17-bit raw CPR longitude** (unsigned integers).
- **Barometric altitude**: the 12-bit altitude field, Q-bit aware (Q set → 25 ft increments:
  `alt = N*25 - 1000` ft), decoded to feet.

It surfaces these as **evidence** (explainability) and as transient **identifiers**
(`cpr_fmt` ∈ {even,odd}, `cpr_lat_raw`, `cpr_lon_raw`, `alt_ft`) the pipeline reads; `FrameType`
becomes `"airborne_position"`. The decoder does **no** lat/lon math and holds **no** state — it only
surfaces the raw values, matching its existing contract. `icao` is still set unconditionally, so
identity/grouping are unchanged. CRC-24 validation is unchanged (invalid frames still rejected).

## Component 2 — `CprPositionResolver` (stateful; the only new stateful unit)

New `src/SignalAtlas.Decode/CprPositionResolver.cs`. The result is a lat/lon pair — reuse the
existing `Position` domain type if it is lat/lon-shaped, otherwise add a small
`GeoPosition(double Lat, double Lon)` record in Domain (the plan confirms which).

```csharp
public interface ICprPositionResolver
{
    /// Feeds one airborne-position frame; returns a decoded (lat, lon) once a consistent even+odd
    /// pair for this ICAO exists within the pairing window, else null. Deterministic (IClock).
    GeoPosition? Accept(string icao, bool odd, int cprLat17, int cprLon17, DateTimeOffset time);
}
```

Holds a **bounded per-ICAO cache** of the last even and last odd frame (each with its timestamp).
On each frame: store by format; if the complementary frame exists and is within the **~10 s pairing
window** (measured via the injected **`IClock`**-sourced timestamps), run the **global airborne CPR**
algorithm:
- `NZ = 15`; `dLat_even = 360/60`, `dLat_odd = 360/59`.
- `j = floor(59·latCprEven − 60·latCprOdd + 0.5)`; `lat_even = dLat_even·(mod(j,60)+latCprEven)`,
  `lat_odd = dLat_odd·(mod(j,59)+latCprOdd)`; normalize ≥270 → −360; use the more-recent frame's lat.
- Compute `NL(lat)` (longitude-zone count). **If `NL(lat_even) ≠ NL(lat_odd)` the pair straddles a
  latitude-zone boundary → discard and await a fresh pair** (return null).
- Longitude from the more-recent frame using `NL`.

Deterministic: same frames + same clock → same position; no wall-clock, no RNG. The cache is bounded
(cap entries; entries older than the window are inert/evicted) so continuous traffic can't grow it
without bound.

## Component 3 — `Device` position, pipeline wiring, persistence

**Domain:** `Device` gains three nullable fields — `double? Latitude, double? Longitude,
int? AltitudeFt`. Nullable so non-aircraft devices and un-fixed aircraft still work. This ripples to
the EF entity, the `/devices` DTO, and the frontend `Device` type (wire contract verified by curling
the real payload before wiring the map — landmine #1).

**Pipeline (`IngestionPipeline` step 4b):** inject `ICprPositionResolver`. For each decoded frame in
an ICAO group with `FrameType == "airborne_position"`, read the transient CPR fields and call
`_cpr.Accept(icao, odd, latRaw, lonRaw, obs.Time)`. On a non-null `GeoPosition`, stamp it plus the
altitude onto the resolved device (`device with { Latitude, Longitude, AltitudeFt }`) before upsert.
On null (no pair yet) the device is still upserted with identity; position fills in on a later frame.
Uses the `IClock`-sourced `obs.Time` — deterministic. The resolver is optional/null-safe like the
other Stage-1 decode deps.

**`DeviceMerge` extended to coalesce position** (same pattern as callsign):
`Latitude/Longitude/AltitudeFt = incoming ?? existing`. A later identity frame doesn't wipe a known
position; a later position frame doesn't wipe a callsign — each accumulates. Raw `cpr_*` transients
are consumed by the pipeline and NOT persisted as device identity (only typed lat/lon/alt + `alt_ft`
persist).

**Persistence:** add the three columns to the EF `Device` mapping + an EF migration; the in-memory
store needs no change. Register `ICprPositionResolver` → `CprPositionResolver` as a singleton and
inject it into both live pipeline build sites (`IqIngressEndpoint.BuildPipeline`,
`PipelineHostedService`), exactly like the Stage-1 decode deps.

## Component 4 — Map aircraft layer & frontend

**`web/src/api.ts` `Device` type** gains `latitude`, `longitude`, `altitudeFt` (matched to the real
payload).

**`RfMap`** adds a second poll of `/devices` alongside the existing `/emitters` poll, filters to
devices with a position, and renders them as a **distinct aircraft layer** — its own GeoJSON source +
a symbol layer with an aircraft glyph (plane/triangle, clearly not an RF-emitter circle). A legend
entry "Aircraft" is added. **No heading rotation and no position trails in v1** (need velocity /
history state — deferred). Auto-fit stays emitter-driven so the viewport doesn't jump to a distant
aircraft.

**Click → aircraft detail:** clicking an aircraft opens the existing right drawer with the device's
fields (callsign title, ICAO, altitude, lat/lon, evidence). The drawer gets a small aircraft-shaped
branch (or a shared detail component) so it serves both emitter and aircraft. Selection keys the
feature `id` against the polled devices, mirroring the emitter-selection pattern.

Untouched: offline graticule basemap, heatmap toggle, theming, and the emitter layer.

## Data flow

```
IQ (1090 MHz) → AdsBDemodulator → 14-byte frame
  → AdsBDecoder: TC 1-4 → callsign; TC 9-18 → cpr_fmt + raw CPR lat/lon + altitude
  → pipeline groups by ICAO → DeviceResolver → Device{icao, callsign}
  → for each airborne-position frame: CprPositionResolver.Accept(icao, odd, latRaw, lonRaw, time)
        → GeoPosition? (non-null once an even+odd pair is consistent)
  → device with { Latitude, Longitude, AltitudeFt } → Upsert (coalescing) + DeviceDetermined
  → /devices → RfMap aircraft layer → plane on the map
```

## Invariants

- **Receive-only (L1):** decode/CPR only read frames; no transmit path.
- **Metadata, not content (L2/L3):** only ICAO, callsign, self-reported position/altitude (cleartext
  control-plane) are parsed; no user payload.
- **Deterministic core (P5):** decoder is pure; resolver uses injected `IClock`, no RNG/wall-clock;
  same frames + clock → same position; device ids unchanged (`DeterministicGuid`).
- **Explainable-only (P4/P6):** position/altitude carry decode evidence; `DeviceResolver` keeps
  evidence non-empty.
- **No new `/api/v1` routes** (the map reuses `/devices` and `/emitters`).

## Testing (deterministic, synthetic, no hardware)

- **`AdsBDecoder` position:** craft valid DF17 TC 9-18 frames (reuse the test's `ModeSCrc` parity
  helper) with known altitude + raw CPR; assert `cpr_fmt`/`cpr_lat_raw`/`cpr_lon_raw`/`alt_ft` +
  evidence; CRC-fail still rejects.
- **`CprPositionResolver`:** a known even+odd pair (documented CPR test vector) decodes to the
  expected lat/lon within tolerance; even-then-odd and odd-then-even both resolve; a single frame →
  null; a stale partner (past the `IClock` window) → null; a latitude-zone-straddling pair →
  discarded; two ICAOs don't cross-contaminate.
- **Pipeline integration:** modulate an even+odd pair for one ICAO through the real
  demod→decode→resolver path (Stage-1 `AdsBModulator`); assert the upserted Aircraft device carries
  the expected lat/lon/altitude and `DeviceDetermined` fires; a partnerless position frame upserts
  identity with null position.
- **`DeviceMerge`:** later identity frame preserves position; later position frame preserves
  callsign (both directions).
- **Persistence:** EF SQLite round-trip of a Device with lat/lon/altitude.
- **Frontend:** `Device` type carries position; the map builds aircraft GeoJSON only from devices
  with a fix (positionless excluded); an aircraft click selects the right device.

## Out of scope (follow-ups)

- Surface position (GNSS-height) TC 20-22; only barometric TC 9-18 in v1.
- Velocity/heading (TC 19), heading-rotated icons, position trails/history.
- Local CPR (receiver-reference) decoding.
- `Signal.DeviceId` / emitter↔device linkage (still deferred from Stage 1).
- Aircraft-specific anomaly/alerting.
