// Config-driven basemap seam for the RF map (SPEC ADR-5 / §4.3).
//
// OFFLINE BY DEFAULT: with no configuration the map uses `blankStyle` — a
// self-contained version-8 style with no tile/sprite/glyph/style URLs — so the
// atlas renders (graticule + emitters) with zero external network access.
//
// To drop in a REAL offline vector basemap (ADR-5, bundled MBTiles/PMTiles):
//   1. Convert your MBTiles to PMTiles and place it at `web/public/basemap.pmtiles`
//      (served at `/basemap.pmtiles`).
//   2. Provide a MapLibre style JSON whose vector source uses the pmtiles://
//      protocol, e.g. `"url": "pmtiles:///basemap.pmtiles"`, plus offline
//      glyphs/sprite you also bundle under web/public.
//   3. Set `VITE_BASEMAP_STYLE` to that style's URL/path (e.g. `/basemap.json`).
// The pmtiles protocol is registered whenever a style is configured, so the
// pmtiles:// source in that style resolves against the bundled archive — still
// no third-party tile servers. Leaving VITE_BASEMAP_STYLE unset keeps the
// blank+graticule offline default and does NOT require the .pmtiles file to
// exist, so the build never depends on shipping one.

import maplibregl, {
  type StyleSpecification,
  type LayerSpecification,
  type RasterSourceSpecification,
} from "maplibre-gl";
import { Protocol } from "pmtiles";
import { blankStyle } from "./rfmap";
import type { ColorMode } from "../theme/palette";

let pmtilesRegistered = false;

/** Register the pmtiles:// protocol on MapLibre once (idempotent). */
export function registerPmtilesProtocol(): void {
  if (pmtilesRegistered) return;
  const protocol = new Protocol();
  maplibregl.addProtocol("pmtiles", protocol.tile);
  pmtilesRegistered = true;
}

/** The configured basemap style URL/path, or undefined for the offline default.
 *  An ESRI basemap id (e.g. "esri-imagery") is NOT a style URL — it is handled by the runtime
 *  switcher (raster layers + defaultBasemapId), so treat it as "no custom style" here. */
export function configuredBasemapStyle(): string | undefined {
  const v = import.meta.env.VITE_BASEMAP_STYLE as string | undefined;
  const trimmed = v?.trim();
  if (!trimmed) return undefined;
  if (ESRI_BASEMAPS.some((b) => b.id === trimmed)) return undefined; // handled by the switcher, not as a style URL
  return trimmed;
}

/** True when a real basemap is configured (emitter/graticule layers sit on top). */
export function hasBasemap(): boolean {
  return configuredBasemapStyle() !== undefined;
}

/**
 * Resolve the map's base style. When `VITE_BASEMAP_STYLE` is set, register the
 * pmtiles protocol (in case the style sources a bundled `.pmtiles`) and return
 * that style's URL. Otherwise fall back to the fully-offline blank style.
 */
export function resolveBaseStyle(mode: ColorMode): string | StyleSpecification {
  const configured = configuredBasemapStyle();
  if (configured) {
    registerPmtilesProtocol();
    return configured;
  }
  return blankStyle(mode);
}

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

/**
 * Default basemap. Online-first: with NO `VITE_BASEMAP_STYLE` set the map opens on the ESRI
 * satellite imagery (the operator is assumed online); "Offline" (blank + graticule) stays one click
 * away in the switcher. If `VITE_BASEMAP_STYLE` names an ESRI id, that id is the default. If it is a
 * real style URL (e.g. a bundled `pmtiles://` vector basemap) we default to "offline" so the ESRI
 * raster layers stay hidden and that configured style shows through — a genuinely offline/air-gapped
 * deployment therefore just sets `VITE_BASEMAP_STYLE` to its bundled style (or "offline" is one click).
 */
export function defaultBasemapId(): BasemapId {
  const v = (import.meta.env.VITE_BASEMAP_STYLE as string | undefined)?.trim();
  if (!v) return "esri-imagery"; // online-first default: satellite
  const match = ESRI_BASEMAPS.find((b) => b.id === v);
  return match ? match.id : "offline"; // a real style URL → let it show; raster switcher stays off
}

/** Attribution text for the active basemap, or null for offline. */
export function attributionFor(id: BasemapId): string | null {
  return ESRI_BASEMAPS.find((b) => b.id === id)?.attribution ?? null;
}
