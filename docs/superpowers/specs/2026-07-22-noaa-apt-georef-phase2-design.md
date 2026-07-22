# NOAA APT Georeference (Phase 2) — Design

**Status:** Approved (fully-autonomous mandate — user reviews merged result).
**Date:** 2026-07-22
**Goal:** Stitch the decoded NOAA APT image onto the RF Map at its real geographic location ("weather map over the grid"), using online orbital data. Builds on Phase 1 (image already decoded + shown in the device drawer).

## 1. Honesty about scope

Pixel-perfect APT georeferencing (per-line scan geometry, Earth curvature, projection warp) is a research-grade problem. Phase 2 ships an **approximate rectangular georef**: the decoded image is placed as a MapLibre image-source quadrilateral aligned to the satellite's ground track over the pass, ±half-swath perpendicular. This is clearly labeled "approximate" in the UI. It is offline-degrading: no TLE / no fix → **no overlay**, and the image still shows in the drawer exactly as in Phase 1.

## 2. Data flow

```
APT device (Phase 1): identifiers.passStart (ISO), lines, satellite name
        │
        ▼
GET /api/v1/devices/{id}/geo   (gated, JSON)
        │  backend: TleProvider (online Celestrak, cached) → Sgp4.Propagate(tle, t)
        │           → sub-satellite track over [passStart, passStart + lines/2 s]
        │           → 4 corner lon/lat (track start/end ± half-swath ⟂ to track)
        ▼
{ corners: [[lon,lat](TL),[lon,lat](TR),[lon,lat](BR),[lon,lat](BL)], approximate: true }  | 404 when no TLE/fix
        │
        ▼
RfMap: add image source (PNG from /devices/{id}/image) at those 4 corners (raster-opacity ~0.75),
       toggled by a "Weather overlay" control; absent when /geo 404s.
```

## 3. Backend components

| Component | Project | Responsibility |
|---|---|---|
| `Tle` record + `ITleProvider` / `CelestrakTleProvider` | `SignalAtlas.Geospatial` | Fetch the NOAA-weather TLE set from Celestrak over HTTP (`https://celestrak.org/NORAD/elements/gp.php?GROUP=noaa&FORMAT=tle`), parse, cache in-memory with a TTL (e.g. 12 h). NORAD ids: NOAA-15=25338, NOAA-18=28654, NOAA-19=33591. Offline / fetch failure → returns null (overlay simply absent). Injected `HttpClient` (mockable). |
| `Sgp4` propagator | `SignalAtlas.Geospatial` | Standard SGP4/SDP4 near-Earth propagation: TLE + UTC time → ECI position → geodetic sub-point (lat/lon). Deterministic pure math (P5). |
| `AptGeoReferencer` | `SignalAtlas.Geospatial` | Given satellite name + passStart + lineCount, propagate the sub-track at a few sample times across the pass, and compute the 4 image-corner lon/lat (track heading → perpendicular ± half-swath, `SwathHalfWidthKm ≈ 1450`). Returns `AptGeoQuad(double[][] Corners, bool Approximate)` or null. |
| `GET /devices/{id}/geo` | `SignalAtlas.Api` | Inside the `api` group (gated). Looks up the APT device (satellite + passStart from its identifiers), calls `AptGeoReferencer`; JSON quad or 404. Audit-logged `read:device-geo`. |

- **SGP4 is the core risk.** Hand-roll a compact, correct SGP4 (near-Earth path is sufficient for NOAA's ~800 km orbit) OR use a permissive MIT/BSD SGP4 source vendored into the project. Either way it MUST be validated: a test with a published TLE + epoch asserts the sub-point against a known-good reference value within tolerance (e.g. <0.1°). Do not ship an unvalidated propagator.
- Deterministic: SGP4 + georef are pure functions of (TLE, time). The TLE *fetch* is I/O (API layer), but given a fixed TLE the geometry is deterministic and testable.

## 4. Frontend

- `web/src/api.ts`: `getDeviceGeo(id): Promise<AptGeoQuad | null>` (envelope-wrapped JSON; 404 → null).
- `web/src/views/RfMap.tsx`: for each `NOAA-APT` device with a `/geo` quad, add a MapLibre `image` source (url = the `/devices/{id}/image` blob object-URL) at the quad corners + a raster layer (opacity ~0.75), above the basemap, below emitter/aircraft points. A "Weather overlay" toggle (like the Heatmap toggle) shows/hides them. Remove sources on unmount/when the device list changes.
- Gracefully absent when `/geo` returns 404 (offline / no TLE / seed device with no real pass).

## 5. Constraints / invariants

- Receive-only, metadata-not-content carve-out unchanged (the image is the same in-memory Phase-1 payload; georef adds only geometry, never persists the image).
- Deterministic core (P5) for SGP4/georef; the online TLE fetch is offline-optional (failure → no overlay; **offline-first preserved** — the map/app never breaks without network).
- Gated `/geo` endpoint (invariant #5). One new dependency is acceptable ONLY if it's a permissive-licensed SGP4 lib; prefer vendoring a compact implementation to keep the dependency surface clean. `HttpClient` for Celestrak.
- Landmine #3 (MapLibre): the image raster layer is standard; the circle-radius rule doesn't apply here.

## 6. Testing

- `Sgp4`: published-TLE-and-epoch → sub-point within tolerance of a reference (the validation gate); determinism.
- `AptGeoReferencer`: a known TLE + passStart → a plausible quad (corners near the sub-track, swath width ~2900 km, ordering TL/TR/BR/BL); null when the sat is unknown.
- `CelestrakTleProvider`: with a mocked `HttpClient` returning a canned TLE set, parses + caches (second call doesn't re-fetch within TTL); fetch failure → null.
- `/devices/{id}/geo`: gated (in the `/api/v1` enumeration); returns a quad for a positioned test device (hub seeded), 404 for an unknown/positionless one.
- Frontend: `getDeviceGeo` (200→quad, 404→null); RfMap overlay test (quad present → image source added; toggle hides it).

## 7. Out of scope

- True per-line projection warp / rectification; multi-pass mosaicking; historical replay. Approximate quad only.
