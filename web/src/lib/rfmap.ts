// Offline MapLibre helpers for the RF map: a fully self-contained blank style
// (no tile/sprite/glyph URLs), emitter GeoJSON, and a meters→pixels radius
// expression so uncertainty circles scale with zoom & latitude. Pure — no React.

import type { StyleSpecification, LayerSpecification } from "maplibre-gl";
import type { FeatureCollection, Point, LineString } from "geojson";
import { chartTokens, protocolColor, type ColorMode } from "../theme/palette";
import type { Emitter, Device, HeatmapCell } from "../api";

export const RF_SOURCE = "emitters";
export const RF_UNCERTAINTY_LAYER = "emitter-uncertainty";
// The heatmap renders SERVER-computed observation density (/map/heatmap → RF_HEATMAP_SOURCE), not the
// correlated emitter points — those are already the markers/rings on RF_SOURCE.
export const RF_HEATMAP_SOURCE = "heatmap-cells";
export const RF_HEATMAP_LAYER = "observation-heatmap";
export const RF_POINT_LAYER = "emitter-points";
export const RF_GRATICULE_SOURCE = "graticule";
export const RF_GRATICULE_LAYER = "graticule-lines";

/** Web-Mercator projects latitudes beyond ±85.0511° to infinity — clamp there. */
const MERCATOR_LAT_LIMIT = 85.0511;

/** Web-Mercator ground resolution at the equator, zoom 0 (m / px). */
const MPP_EQUATOR_Z0 = 156543.03392;

/** Meters → pixels at a given latitude & zoom (Web-Mercator ground scale). */
export function metersToPixels(meters: number, latitude: number, zoom: number): number {
  const cosLat = Math.max(Math.cos((latitude * Math.PI) / 180), 1e-6);
  const mpp = (MPP_EQUATOR_Z0 * cosLat) / Math.pow(2, zoom);
  return meters / mpp;
}

/**
 * Blank, fully-offline style: a single solid background layer painted with the
 * chart page-plane color. No sources, glyphs, sprite, or tile URLs — renders
 * with zero network access.
 */
export function blankStyle(mode: ColorMode): StyleSpecification {
  return {
    version: 8,
    sources: {},
    layers: [
      {
        id: "page-plane",
        type: "background",
        paint: { "background-color": chartTokens[mode].surfaceAlt },
      },
    ],
  };
}

export interface EmitterFeatureProps {
  id: string;
  protocol: string;
  color: string;
  uncertaintyM: number;
  cosLat: number;
  /** Uncertainty radius in pixels at zoom 0 (uncertaintyM / (MPP_EQUATOR_Z0·cosLat)). */
  rBase: number;
  confidence: number;
}

/** Split emitters into a placeable GeoJSON FC (colored per mode) + skip count. */
export function emittersToGeoJSON(
  emitters: Emitter[],
  mode: ColorMode,
): { fc: FeatureCollection<Point, EmitterFeatureProps>; unplaceable: number } {
  let unplaceable = 0;
  const features: FeatureCollection<Point, EmitterFeatureProps>["features"] = [];
  for (const e of emitters) {
    const lat = e.estLatitude;
    const lon = e.estLongitude;
    // Skip emitters without a fix — coords are nullable on the API type.
    if (lat === null || lon === null || !Number.isFinite(lat) || !Number.isFinite(lon)) {
      unplaceable += 1;
      continue;
    }
    const uncertaintyM = Math.max(e.estUncertaintyM ?? 0, 0);
    const cosLat = Math.max(Math.cos((lat * Math.PI) / 180), 1e-6);
    features.push({
      type: "Feature",
      geometry: { type: "Point", coordinates: [lon, lat] },
      properties: {
        id: e.id,
        protocol: e.protocol,
        color: protocolColor(e.protocol, mode),
        uncertaintyM,
        cosLat,
        rBase: uncertaintyM / (MPP_EQUATOR_Z0 * cosLat),
        confidence: e.confidence,
      },
    });
  }
  return { fc: { type: "FeatureCollection", features }, unplaceable };
}

/**
 * circle-radius (px) = rBase · 2^zoom, where rBase is the ring's pixel radius at
 * zoom 0. MapLibre only allows `zoom` inside a top-level step/interpolate, so we
 * express the 2^zoom scaling as an exponential-base-2 interpolation across the
 * zoom range [0,24] (which yields exactly rBase·2^zoom). Floored at 2px.
 */
const uncertaintyRadiusExpr = [
  // Must be a TOP-LEVEL interpolate on zoom (MapLibre rule); the 2px floor lives
  // in the stop outputs, not an outer wrapper.
  "interpolate",
  ["exponential", 2],
  ["zoom"],
  0,
  ["max", 2, ["get", "rBase"]],
  24,
  ["max", 2, ["*", ["get", "rBase"], 16777216]], // rBase · 2^24
];

/** Emitter layer stack on RF_SOURCE: uncertainty rings (bottom) → point markers. The observation
 * heatmap lives on its own source/layer (see {@link heatmapLayer}) so density and correlated
 * emitters are decoupled. */
export function rfLayers(mode: ColorMode): LayerSpecification[] {
  const surface = chartTokens[mode].surface;
  const uncertainty = {
    id: RF_UNCERTAINTY_LAYER,
    type: "circle",
    source: RF_SOURCE,
    paint: {
      "circle-radius": uncertaintyRadiusExpr,
      "circle-color": ["get", "color"],
      "circle-opacity": 0.18,
      "circle-stroke-color": ["get", "color"],
      "circle-stroke-width": 1,
      "circle-stroke-opacity": 0.5,
    },
  };
  const points = {
    id: RF_POINT_LAYER,
    type: "circle",
    source: RF_SOURCE,
    paint: {
      "circle-radius": 6,
      "circle-color": ["get", "color"],
      "circle-stroke-color": surface,
      "circle-stroke-width": 2,
    },
  };
  return [uncertainty, points] as unknown as LayerSpecification[];
}

export interface HeatmapCellProps {
  count: number;
  /** Heatmap weight = observation count, floored at 1 (drives `heatmap-weight`). */
  weight: number;
}

/**
 * Server density cells (`/map/heatmap`) → point GeoJSON weighted by observation count. Cells with a
 * non-finite centre are skipped; the weight is floored at 1 so a single-observation cell still
 * paints. Pure so the mapping is unit-testable without a live MapLibre instance.
 */
export function heatmapCellsToGeoJSON(
  cells: HeatmapCell[],
): FeatureCollection<Point, HeatmapCellProps> {
  const features: FeatureCollection<Point, HeatmapCellProps>["features"] = [];
  for (const c of cells) {
    if (!Number.isFinite(c.lat) || !Number.isFinite(c.lon)) continue;
    const weight = Math.max(c.count, 1);
    features.push({
      type: "Feature",
      geometry: { type: "Point", coordinates: [c.lon, c.lat] },
      properties: { count: c.count, weight },
    });
  }
  return { type: "FeatureCollection", features };
}

/**
 * Observation-density heatmap layer, fed by the server `/map/heatmap` cells (RF_HEATMAP_SOURCE).
 * Hidden until the operator toggles it on. `heatmap-weight` maps each cell's observation count onto
 * [0,1] (single obs → light, ~20+ → full) so a busy cell dominates without one outlier washing the
 * rest out.
 */
export function heatmapLayer(): LayerSpecification {
  return {
    id: RF_HEATMAP_LAYER,
    type: "heatmap",
    source: RF_HEATMAP_SOURCE,
    layout: { visibility: "none" },
    paint: {
      "heatmap-weight": ["interpolate", ["linear"], ["get", "weight"], 0, 0, 1, 0.4, 20, 1],
      "heatmap-intensity": 1,
      "heatmap-radius": 36,
      "heatmap-opacity": 0.75,
    },
  } as unknown as LayerSpecification;
}

/**
 * Adaptive graticule spacing (degrees) by zoom. Coarser far out, finer as you
 * zoom in, so the grid stays legible and cheap to generate. Breakpoints chosen
 * so a screen never holds more than a few dozen lines per axis.
 */
export function graticuleSpacing(zoom: number): number {
  if (zoom < 4) return 10; // continents
  if (zoom < 7) return 1; // 1°
  if (zoom < 11) return 0.1; // 0.1°
  return 0.01; // 0.01° (city / block scale)
}

/** Snap a value to the nearest multiple of `step` (kills float drift). */
function snap(v: number, step: number): number {
  return Math.round(v / step) * step;
}
function round6(v: number): number {
  return Math.round(v * 1e6) / 1e6;
}

export interface ViewBounds {
  west: number;
  south: number;
  east: number;
  north: number;
}

/**
 * Build a graticule (meridians + parallels) as LineString features covering the
 * given viewport at `spacing` degrees. Meridians/parallels are straight in
 * Web-Mercator, so two endpoints each suffice. Clamped to the world & Mercator
 * latitude limits; line counts are bounded so an over-wide viewport can't blow
 * up geometry. Purely generated — no tiles, no network.
 */
export function buildGraticule(bounds: ViewBounds, spacing: number): FeatureCollection<LineString> {
  const west = Math.max(bounds.west, -180);
  const east = Math.min(bounds.east, 180);
  const south = Math.max(bounds.south, -MERCATOR_LAT_LIMIT);
  const north = Math.min(bounds.north, MERCATOR_LAT_LIMIT);
  const features: FeatureCollection<LineString>["features"] = [];
  const MAX_LINES = 512; // safety cap per axis

  // Meridians (constant lon, span full visible lat range).
  let count = 0;
  for (let lon = snap(west, spacing); lon <= east && count < MAX_LINES; lon += spacing, count++) {
    features.push({
      type: "Feature",
      properties: {},
      geometry: {
        type: "LineString",
        coordinates: [
          [round6(lon), round6(south)],
          [round6(lon), round6(north)],
        ],
      },
    });
  }
  // Parallels (constant lat, span full visible lon range).
  count = 0;
  for (let lat = snap(south, spacing); lat <= north && count < MAX_LINES; lat += spacing, count++) {
    const clat = Math.max(-MERCATOR_LAT_LIMIT, Math.min(MERCATOR_LAT_LIMIT, lat));
    features.push({
      type: "Feature",
      properties: {},
      geometry: {
        type: "LineString",
        coordinates: [
          [round6(west), round6(clat)],
          [round6(east), round6(clat)],
        ],
      },
    });
  }
  return { type: "FeatureCollection", features };
}

/**
 * Recessive graticule line layer, themed with the chart gridline token. Thin &
 * low-opacity so emitters always dominate the plane.
 */
export function graticuleLayer(mode: ColorMode): LayerSpecification {
  return {
    id: RF_GRATICULE_LAYER,
    type: "line",
    source: RF_GRATICULE_SOURCE,
    paint: {
      "line-color": chartTokens[mode].grid,
      "line-width": 0.5,
      "line-opacity": 0.7,
    },
  } as unknown as LayerSpecification;
}

export const AIRCRAFT_SOURCE = "aircraft";
export const AIRCRAFT_POINT_LAYER = "aircraft-points";

/** Distinct aircraft marker color (amber) — deliberately NOT a protocol color. */
export const AIRCRAFT_COLOR = "#f5a623";

export interface AircraftFeatureProps {
  id: string;
  callsign: string;
  icao: string;
  altitudeFt: number | null;
}

/** Aircraft (devices with a self-reported CPR fix) → point GeoJSON; positionless devices excluded. */
export function aircraftToGeoJSON(
  devices: Device[],
): FeatureCollection<Point, AircraftFeatureProps> {
  const features: FeatureCollection<Point, AircraftFeatureProps>["features"] = [];
  for (const d of devices) {
    const lat = d.latitude;
    const lon = d.longitude;
    if (lat === null || lon === null || !Number.isFinite(lat) || !Number.isFinite(lon)) continue;
    features.push({
      type: "Feature",
      geometry: { type: "Point", coordinates: [lon, lat] },
      properties: {
        id: d.id,
        callsign: d.identifiers?.callsign ?? "",
        icao: d.identifiers?.icao ?? d.id,
        altitudeFt: d.altitudeFt,
      },
    });
  }
  return { type: "FeatureCollection", features };
}

/**
 * Coordinates the initial map view should fit — emitters AND aircraft together. Fitting to
 * emitters alone can leave positioned aircraft (which may sit nowhere near the emitters) off the
 * viewport permanently, since the view is only auto-fitted once. Pure so the "did we actually
 * include the aircraft" behavior is unit-testable without a live MapLibre instance.
 */
export function collectFitCoordinates(
  emitterFc: FeatureCollection<Point, EmitterFeatureProps>,
  aircraftFc: FeatureCollection<Point, AircraftFeatureProps>,
): [number, number][] {
  const coords: [number, number][] = [];
  for (const f of emitterFc.features) coords.push(f.geometry.coordinates as [number, number]);
  for (const f of aircraftFc.features) coords.push(f.geometry.coordinates as [number, number]);
  return coords;
}

/** Aircraft marker layer — a distinct amber circle (offline style has no sprites/glyphs for icons). */
export function aircraftLayer(mode: ColorMode): LayerSpecification {
  return {
    id: AIRCRAFT_POINT_LAYER,
    type: "circle",
    source: AIRCRAFT_SOURCE,
    paint: {
      "circle-radius": 5,
      "circle-color": AIRCRAFT_COLOR,
      "circle-stroke-color": chartTokens[mode].surface,
      "circle-stroke-width": 2,
    },
  } as unknown as LayerSpecification;
}
