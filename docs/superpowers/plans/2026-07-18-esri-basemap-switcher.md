# RF Map ESRI Basemap Switcher Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a runtime basemap switcher to the RF map — Offline / ESRI Satellite / Street / Topo — toggling raster-layer visibility without tearing down the RF data layers.

**Architecture:** A typed basemap registry in `basemap.ts` defines three ESRI World_* raster sources/layers (hidden by default). `RfMap.tsx` adds them at map load below the graticule, and a legend `ToggleButtonGroup` flips their `visibility`. No `setStyle` — emitter/aircraft/graticule layers persist. Offline-first: zero ESRI calls until a basemap is selected.

**Tech Stack:** React + TypeScript, MapLibre GL (raster sources), MUI (`ToggleButtonGroup`), Vitest.

## Global Constraints

- **Frontend only.** No backend, no new `/api/v1` routes.
- **Offline-first preserved:** ESRI raster layers are created `visibility: "none"`; default selection is `"offline"` unless `VITE_BASEMAP_STYLE` names an ESRI id — an unconfigured build makes zero external calls. Only z/x/y tile coords ever leave; no RF data (egress guard / metadata-only untouched).
- **No `setStyle`** for switching (it would tear down the emitter/aircraft/graticule layers). Switch via `map.setLayoutProperty(layerId, "visibility", ...)`.
- **ESRI tile templates** (raster, `{z}/{y}/{x}` order, 256 px, maxzoom 19):
  - Imagery: `https://server.arcgisonline.com/ArcGIS/rest/services/World_Imagery/MapServer/tile/{z}/{y}/{x}`
  - Street: `https://server.arcgisonline.com/ArcGIS/rest/services/World_Street_Map/MapServer/tile/{z}/{y}/{x}`
  - Topo: `https://server.arcgisonline.com/ArcGIS/rest/services/World_Topo_Map/MapServer/tile/{z}/{y}/{x}`
- **ESRI attribution shown** when an ESRI basemap is active (ToS).
- **Do not disturb** the existing emitter/aircraft/graticule layers, their click/drawer handlers, the heatmap toggle, `page-plane` theming, auto-fit, or the pmtiles/`resolveBaseStyle` seam.
- **Verify each task:** `cd web && npm run test && npm run build` (`tsc -b && vite build`; no separate lint script). MapLibre doesn't render under jsdom — keep automated tests at the pure-function/registry level (per the Stage-2 rfmap precedent).

## File Structure

- **Modify** `web/src/lib/basemap.ts` — add `BasemapId`, `EsriBasemap`, `esriBasemaps()`, `rasterSourcesAndLayers()`, `basemapLabels()`, `defaultBasemapId()`, `attributionFor()`. (Task 1)
- **Create** `web/src/lib/basemap.test.ts` — registry unit tests. (Task 1)
- **Modify** `web/src/views/RfMap.tsx` — raster layers at load, selection state, visibility effect, legend `ToggleButtonGroup`, attribution caption. (Task 2)

---

### Task 1: Basemap registry (`basemap.ts`)

**Files:**
- Modify: `web/src/lib/basemap.ts`
- Test: `web/src/lib/basemap.test.ts`

**Interfaces:**
- Produces: `type BasemapId = "offline" | "esri-imagery" | "esri-street" | "esri-topo"`; `interface EsriBasemap`; `esriBasemaps(): EsriBasemap[]`; `rasterSourcesAndLayers(): { source: RasterSourceSpecification; layer: LayerSpecification }[]`; `basemapLabels(): { id: BasemapId; label: string }[]`; `defaultBasemapId(): BasemapId`; `attributionFor(id: BasemapId): string | null`.

- [ ] **Step 1: Write the failing test**

Create `web/src/lib/basemap.test.ts`:

```typescript
import { describe, it, expect } from "vitest";
import {
  esriBasemaps,
  rasterSourcesAndLayers,
  basemapLabels,
  defaultBasemapId,
  attributionFor,
} from "./basemap";

describe("ESRI basemap registry", () => {
  it("defines three ESRI raster basemaps with World_* tile templates", () => {
    const b = esriBasemaps();
    expect(b.map((x) => x.id).sort()).toEqual(["esri-imagery", "esri-street", "esri-topo"]);
    for (const x of b) {
      expect(x.tiles[0]).toMatch(/server\.arcgisonline\.com\/ArcGIS\/rest\/services\/World_/);
      expect(x.tiles[0]).toContain("/tile/{z}/{y}/{x}");
      expect(x.attribution.length).toBeGreaterThan(0);
      expect(x.maxzoom).toBeGreaterThan(0);
    }
    expect(new Set(b.map((x) => x.sourceId)).size).toBe(3); // unique source ids
    expect(new Set(b.map((x) => x.layerId)).size).toBe(3);  // unique layer ids
  });

  it("builds hidden raster sources+layers", () => {
    const pairs = rasterSourcesAndLayers();
    expect(pairs).toHaveLength(3);
    for (const p of pairs) {
      expect((p.source as { type: string }).type).toBe("raster");
      expect((p.source as { tileSize: number }).tileSize).toBe(256);
      expect((p.layer as { type: string }).type).toBe("raster");
      expect((p.layer as { layout?: { visibility?: string } }).layout?.visibility).toBe("none");
    }
  });

  it("lists Offline first then the three ESRI labels", () => {
    const labels = basemapLabels();
    expect(labels[0]).toEqual({ id: "offline", label: "Offline" });
    expect(labels.map((l) => l.id)).toEqual(["offline", "esri-imagery", "esri-street", "esri-topo"]);
  });

  it("defaults to offline and reports attribution per basemap", () => {
    expect(defaultBasemapId()).toBe("offline"); // VITE_BASEMAP_STYLE unset in test env
    expect(attributionFor("offline")).toBeNull();
    expect(attributionFor("esri-imagery")).not.toBeNull();
  });
});
```

- [ ] **Step 2: Run it to verify it fails**

Run: `cd /c/GIT/SignalAtlas/web && npx vitest run src/lib/basemap.test.ts`
Expected: FAIL — the new exports don't exist.

- [ ] **Step 3: Implement the registry**

In `web/src/lib/basemap.ts`, add the import type and the registry (append after the existing exports; extend the existing `maplibre-gl` import to include the raster/layer spec types):

```typescript
import type {
  StyleSpecification,
  LayerSpecification,
  RasterSourceSpecification,
} from "maplibre-gl";

// ... existing code (registerPmtilesProtocol, configuredBasemapStyle, hasBasemap, resolveBaseStyle) ...

export type BasemapId = "offline" | "esri-imagery" | "esri-street" | "esri-topo";

export interface EsriBasemap {
  id: Exclude<BasemapId, "offline">;
  label: string;
  sourceId: string;
  layerId: string;
  tiles: string[];
  attribution: string;
  maxzoom: number;
}

const ESRI_BASE = "https://server.arcgisonline.com/ArcGIS/rest/services";

const ESRI_BASEMAPS: EsriBasemap[] = [
  {
    id: "esri-imagery",
    label: "Satellite",
    sourceId: "esri-imagery-src",
    layerId: "esri-imagery-layer",
    tiles: [`${ESRI_BASE}/World_Imagery/MapServer/tile/{z}/{y}/{x}`],
    attribution: "Powered by Esri — Source: Esri, Maxar, Earthstar Geographics, and the GIS User Community",
    maxzoom: 19,
  },
  {
    id: "esri-street",
    label: "Street",
    sourceId: "esri-street-src",
    layerId: "esri-street-layer",
    tiles: [`${ESRI_BASE}/World_Street_Map/MapServer/tile/{z}/{y}/{x}`],
    attribution: "Powered by Esri — Source: Esri, HERE, Garmin, USGS, NGA, EPA, USDA, NPS",
    maxzoom: 19,
  },
  {
    id: "esri-topo",
    label: "Topo",
    sourceId: "esri-topo-src",
    layerId: "esri-topo-layer",
    tiles: [`${ESRI_BASE}/World_Topo_Map/MapServer/tile/{z}/{y}/{x}`],
    attribution: "Powered by Esri — Source: Esri, HERE, Garmin, FAO, NOAA, USGS, © OpenStreetMap contributors",
    maxzoom: 19,
  },
];

/** The three ESRI raster basemap definitions. */
export function esriBasemaps(): EsriBasemap[] {
  return ESRI_BASEMAPS;
}

/** Raster source+layer pairs for each ESRI basemap; layers start hidden (offline-first).
 *  `sourceId` is the id the map wiring passes to `map.addSource(sourceId, source)`. */
export function rasterSourcesAndLayers(): { sourceId: string; source: RasterSourceSpecification; layer: LayerSpecification }[] {
  return ESRI_BASEMAPS.map((b) => ({
    sourceId: b.sourceId,
    source: {
      type: "raster",
      tiles: b.tiles,
      tileSize: 256,
      maxzoom: b.maxzoom,
      attribution: b.attribution,
    } as RasterSourceSpecification,
    layer: {
      id: b.layerId,
      type: "raster",
      source: b.sourceId,
      layout: { visibility: "none" },
    } as unknown as LayerSpecification,
  }));
}

/** Switcher options: Offline first, then the three ESRI basemaps. */
export function basemapLabels(): { id: BasemapId; label: string }[] {
  return [{ id: "offline" as BasemapId, label: "Offline" }, ...ESRI_BASEMAPS.map((b) => ({ id: b.id as BasemapId, label: b.label }))];
}

/** Default basemap: an ESRI id iff VITE_BASEMAP_STYLE names one, else offline (no external calls). */
export function defaultBasemapId(): BasemapId {
  const v = (import.meta.env.VITE_BASEMAP_STYLE as string | undefined)?.trim();
  const match = ESRI_BASEMAPS.find((b) => b.id === v);
  return match ? match.id : "offline";
}

/** Attribution text for the active basemap, or null for offline. */
export function attributionFor(id: BasemapId): string | null {
  return ESRI_BASEMAPS.find((b) => b.id === id)?.attribution ?? null;
}
```

(Each pair's `layer.source` is `b.sourceId`; Task 2 calls `map.addSource(pair.sourceId, pair.source)` then `map.addLayer(pair.layer)`. The Step-1 test reads only `.source` and `.layer`, so the extra `sourceId` field doesn't affect it.)

- [ ] **Step 4: Run the test to verify it passes**

Run: `cd /c/GIT/SignalAtlas/web && npx vitest run src/lib/basemap.test.ts`
Expected: PASS.

- [ ] **Step 5: Commit**

```bash
cd /c/GIT/SignalAtlas && git add web/src/lib/basemap.ts web/src/lib/basemap.test.ts
git commit -m "feat(web): ESRI raster basemap registry (imagery/street/topo)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: Map wiring & switcher (`RfMap.tsx`)

**Files:**
- Modify: `web/src/views/RfMap.tsx`

**Interfaces:**
- Consumes: `rasterSourcesAndLayers`, `basemapLabels`, `defaultBasemapId`, `attributionFor`, `type BasemapId` from `../lib/basemap`.

- [ ] **Step 1: Add imports + selection state**

In `web/src/views/RfMap.tsx`:
- Add MUI imports near the other MUI imports:
```tsx
import ToggleButton from "@mui/material/ToggleButton";
import ToggleButtonGroup from "@mui/material/ToggleButtonGroup";
```
- Extend the existing `../lib/basemap` import:
```tsx
import {
  resolveBaseStyle,
  rasterSourcesAndLayers,
  basemapLabels,
  defaultBasemapId,
  attributionFor,
  type BasemapId,
} from "../lib/basemap";
```
- Add selection state next to the other `useState` hooks (near `const [heatmap, setHeatmap] = useState(false);`):
```tsx
  const [basemap, setBasemap] = useState<BasemapId>(defaultBasemapId());
```

- [ ] **Step 2: Add ESRI raster sources+layers at map load (below the graticule)**

In the `map.on("load", () => {` handler, insert BEFORE the existing `map.addSource(RF_GRATICULE_SOURCE, {` line (so basemaps render at the bottom, under graticule/emitters/aircraft):

```tsx
      // ESRI raster basemaps (hidden until selected — offline-first). Added first so the graticule,
      // emitters, and aircraft layers stack above whatever basemap is active.
      for (const pair of rasterSourcesAndLayers()) {
        map.addSource(pair.sourceId, pair.source);
        map.addLayer(pair.layer);
      }
```

- [ ] **Step 3: Add the visibility effect**

Add a new `useEffect` next to the existing "Heatmap layer visibility toggle" effect:

```tsx
  // Basemap switcher: flip ESRI raster-layer visibility. Offline → all hidden (blank plane + graticule).
  useEffect(() => {
    const map = mapRef.current;
    if (!map || !mapReady) return;
    for (const { id } of basemapLabels()) {
      if (id === "offline") continue;
      const layerId = `${id}-layer`; // registry layerId, e.g. "esri-imagery" → "esri-imagery-layer"
      if (map.getLayer(layerId)) {
        map.setLayoutProperty(layerId, "visibility", basemap === id ? "visible" : "none");
      }
    }
  }, [basemap, mapReady]);
```

- [ ] **Step 4: Add the switcher to the legend**

In the `ChartCard` `legend={ ... }` Box (which holds the Heatmap `FormControlLabel` + `ProtocolLegend`), add the switcher as the first child:

```tsx
          <ToggleButtonGroup
            size="small"
            exclusive
            value={basemap}
            onChange={(_, v) => { if (v) setBasemap(v as BasemapId); }}
            aria-label="Basemap"
          >
            {basemapLabels().map((b) => (
              <ToggleButton key={b.id} value={b.id} sx={{ textTransform: "none", px: 1 }}>
                {b.label}
              </ToggleButton>
            ))}
          </ToggleButtonGroup>
```

- [ ] **Step 5: Add the attribution caption**

In the map container `Box` (the one holding the "unplaceable emitters" caption), add an ESRI attribution caption (bottom-right so it doesn't collide with the bottom-left unplaceable caption or the ScaleControl — use bottom-right above the ScaleControl, or bottom-left if clearer; place bottom-left under the unplaceable one):

```tsx
        {attributionFor(basemap) && (
          <Box
            sx={{
              position: "absolute",
              right: 8,
              bottom: 8,
              px: 1,
              py: 0.25,
              borderRadius: 1,
              bgcolor: "background.paper",
              border: 1,
              borderColor: "divider",
              maxWidth: "70%",
            }}
          >
            <Typography variant="caption" color="text.secondary" sx={{ fontSize: 10 }}>
              {attributionFor(basemap)}
            </Typography>
          </Box>
        )}
```

- [ ] **Step 6: Run the full frontend suite + build**

Run: `cd /c/GIT/SignalAtlas/web && npm run test && npm run build`
Expected: all tests PASS (the Task-1 registry tests + existing suite); `tsc -b && vite build` clean.

- [ ] **Step 7: Commit**

```bash
cd /c/GIT/SignalAtlas && git add web/src/views/RfMap.tsx
git commit -m "feat(web): RF map basemap switcher (Offline / ESRI satellite/street/topo)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Self-Review

**1. Spec coverage:**
- ESRI registry (`esriBasemaps`/`rasterSourcesAndLayers`/`basemapLabels`/`defaultBasemapId`/`attributionFor`) → Task 1. ✓
- Raster layers added hidden at load below graticule; visibility-toggle switch (no setStyle) → Task 2 Steps 2-3. ✓
- Legend `ToggleButtonGroup` (Offline/Satellite/Street/Topo) → Task 2 Step 4. ✓
- Offline-first default + attribution → `defaultBasemapId` (Task 1) + caption (Task 2 Step 5). ✓
- Emitter/aircraft/graticule/heatmap/theming untouched (additive edits only) → Task 2 inserts, no removals. ✓
- Tests at registry level (MapLibre not driven in jsdom) → Task 1 test. ✓
- Out-of-scope (pmtiles bundle, re-theming, persistence, other providers) → not implemented. ✓

**2. Placeholder scan:** No TBD/TODO/"handle errors". All code shown. The one note (Task 2 Step 3 layer-id) gives the exact final form (`` `${id}-layer` ``) rather than the confusing `.replace` — implementer uses the template literal directly.

**3. Type consistency:** `BasemapId` used identically in Tasks 1/2. `rasterSourcesAndLayers()` returns `{ sourceId, source, layer }` (final form in Task 1 Step 3) — Task 2 Step 2 reads `pair.sourceId`/`pair.source`/`pair.layer`. Layer ids are `` `${id}-layer` `` in both the registry (`layerId`) and the visibility effect. `basemapLabels()`/`defaultBasemapId()`/`attributionFor()` signatures identical across tasks. ESRI tile templates identical to Global Constraints.
