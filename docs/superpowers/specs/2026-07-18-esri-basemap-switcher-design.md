# RF Map ESRI Basemap Switcher — Design

**Date:** 2026-07-18
**Status:** Approved for planning (user directed autonomous execution)
**Scope:** Frontend only (`web/`). No backend changes.

## Summary

Add a selectable real basemap to the RF map. Three ESRI World_* raster tile services
(Imagery/Street/Topo) plus the existing offline blank+graticule are switchable at runtime via a
`ToggleButtonGroup` in the map legend. Basemap layers live in the style (hidden by default) and are
toggled by visibility — never via `setStyle` — so the emitter/aircraft/graticule layers are never
torn down. Offline-first is preserved: zero ESRI network calls until the operator selects a basemap.

## Motivation

The RF map renders only the offline blank graticule (ADR-5, offline-first). Emitters and (Stage 2)
aircraft float on a blank plane with no geographic context. ESRI publishes free World_* raster tile
services that drop into MapLibre as raster sources — worldwide, no data to bundle, no API key for
light use — giving satellite/street/topo context. The `pmtiles`/`VITE_BASEMAP_STYLE` seam already
exists; this adds an ESRI raster path + a runtime switcher on top of it.

## Chosen approach (A)

All three ESRI raster layers live in the map style, initially hidden; a legend `ToggleButtonGroup`
switches among Offline/Imagery/Street/Topo by flipping raster-layer `visibility`. No `setStyle` (which
would tear down and force a re-add of the emitter/aircraft/graticule layers and flicker RF data).
Rejected: (B) `setStyle` per basemap — fragile, reloads data each switch; (C) an on-map MapLibre
custom control — more boilerplate, harder to test than a legend toggle that mirrors the existing
Heatmap toggle.

**Offline-first / invariants.** ESRI tiles are an OPT-IN online source; the default is Offline
(unless `VITE_BASEMAP_STYLE` names an ESRI id), so an unconfigured atlas makes zero external calls.
Only basemap tile requests (z/x/y coordinates) leave — no RF data, so the M13 egress guard and the
metadata-only invariant are untouched. ESRI ToS attribution is shown whenever an ESRI layer is
active.

## Component 1 — Basemap registry (`web/src/lib/basemap.ts`)

A typed registry; the switcher and map wiring derive from it.

```typescript
export type BasemapId = "offline" | "esri-imagery" | "esri-street" | "esri-topo";

export interface EsriBasemap {
  id: BasemapId;        // never "offline" for these three
  label: string;        // switcher label: "Satellite" / "Street" / "Topo"
  sourceId: string;     // e.g. "esri-imagery-src"
  layerId: string;      // e.g. "esri-imagery-layer"
  tiles: string[];      // ESRI World_* raster template ({z}/{y}/{x} order)
  attribution: string;  // ESRI ToS attribution text
  maxzoom: number;      // ~19
}
```

New exports:
- `esriBasemaps(): EsriBasemap[]` — the three raster definitions. Tile templates:
  `https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}`
  (and `World_Street_Map`, `World_Topo_Map`), 256 px, `maxzoom: 19`. Attribution strings per service
  (e.g. imagery: `"Powered by Esri — Source: Esri, Maxar, Earthstar Geographics, and the GIS User
  Community"`).
- `rasterSourcesAndLayers()` — returns three `{ source: RasterSourceSpecification, layer:
  LayerSpecification }` pairs; each layer created with `layout: { visibility: "none" }` (nothing
  loads until selected). Raster source carries its `attribution` and `maxzoom`.
- `basemapLabels(): { id: BasemapId; label: string }[]` — the switcher's four options, "Offline"
  first, then the three ESRI labels.
- `defaultBasemapId(): BasemapId` — if `VITE_BASEMAP_STYLE` (trimmed) equals a known ESRI id, that;
  else `"offline"`. (A custom URL/pmtiles value is NOT an ESRI id → default `"offline"`, and the
  existing `resolveBaseStyle`/pmtiles path is left intact for advanced offline users.)
- `attributionFor(id: BasemapId): string | null` — the active basemap's attribution, or null for
  offline.

`resolveBaseStyle`/`blankStyle`/`configuredBasemapStyle`/`hasBasemap`/`registerPmtilesProtocol`
stay as-is; the ESRI switcher builds on the blank style path (the map style is still the blank
offline style; the ESRI raster sources/layers are added at map load, below the graticule).

## Component 2 — Map wiring & switcher (`web/src/views/RfMap.tsx`)

- **Selection state:** `const [basemap, setBasemap] = useState<BasemapId>(defaultBasemapId())`.
- **At `map.on("load")`, before the graticule** (so basemaps sit at the bottom): for each
  `rasterSourcesAndLayers()` pair, `map.addSource(pair.source...)` + `map.addLayer(pair.layer)` (all
  hidden). Then the existing graticule → emitter → aircraft layers add on top, unchanged.
- **Visibility effect:** a `useEffect` on `[basemap, mapReady]` sets, for each ESRI layer,
  `map.setLayoutProperty(layerId, "visibility", basemap === id ? "visible" : "none")`. Offline → all
  hidden (the blank `page-plane` background + graticule show).
- **Switcher UI:** a MUI `ToggleButtonGroup` (exclusive, size small) in the ChartCard `legend` Box,
  next to the existing Heatmap `Switch` and `ProtocolLegend`, with the four `basemapLabels()` options;
  `onChange` → `setBasemap`.
- **Attribution:** a small caption overlay (bottom-left of the map container, same styling as the
  existing "unplaceable emitters" caption) rendering `attributionFor(basemap)` when non-null. Fulfils
  ESRI ToS.

Untouched: emitter/aircraft layers and their click/drawer handlers, the graticule, the heatmap
toggle, offline `page-plane` theming, auto-fit, and dark/light theming.

## Edge cases

- **Offline / ESRI unreachable:** raster tiles simply fail to load (MapLibre shows blank tiles); the
  map, graticule, emitters, and aircraft still render. No crash.
- **Default:** Offline unless `VITE_BASEMAP_STYLE` names an ESRI id — an unconfigured build makes no
  external calls (offline-first honored).
- **Contrast:** graticule/emitter/aircraft colors keep their existing tokens over imagery (acceptable
  for v1; per-basemap re-theming is out of scope).
- **No persistence:** the selection resets to the default on reload (localStorage persistence is out
  of scope, YAGNI).

## Invariants

- Frontend only; no backend, no new `/api/v1` routes.
- Offline-first preserved: zero external calls until the operator opts into an ESRI basemap; RF data
  never leaves (only z/x/y tile coords) — egress guard / metadata-only untouched.
- No prime-invariant surface (receive-only, deterministic core, auth-gating) is affected.

## Testing (Vitest — pure-function/registry level; MapLibre-in-jsdom is not driven)

- `web/src/lib/basemap.test.ts`:
  - `esriBasemaps()` returns three entries with `server.arcgisonline.com/.../World_{Imagery,Street_Map,Topo_Map}/.../{z}/{y}/{x}` tile templates, non-empty attribution, and unique source/layer ids.
  - `rasterSourcesAndLayers()` returns raster layers all with `visibility: "none"` and raster sources with `type: "raster"`, `tileSize: 256`.
  - `defaultBasemapId()` returns `"offline"` when `VITE_BASEMAP_STYLE` is unset/custom-URL and the matching id when it names an ESRI basemap.
  - `basemapLabels()` lists Offline first + the three ESRI labels; `attributionFor("offline")` is null, `attributionFor("esri-imagery")` is non-null.
- A component-level test is kept out (MapLibre doesn't render under jsdom, per the Stage-2 rfmap
  precedent); the visibility mapping (`basemap === id ? "visible" : "none"`) is trivial and covered by
  the registry tests + the build's type check.

## Out of scope (follow-ups)

- Bundled offline pmtiles/vector basemap (the existing seam remains for that).
- Per-basemap re-theming of graticule/emitter colors; basemap opacity slider.
- Persisting the basemap choice (localStorage).
- Additional providers (OSM/Carto/MapTiler) or a full basemap gallery.
