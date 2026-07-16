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

import maplibregl, { type StyleSpecification } from "maplibre-gl";
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

/** The configured basemap style URL/path, or undefined for the offline default. */
export function configuredBasemapStyle(): string | undefined {
  const v = import.meta.env.VITE_BASEMAP_STYLE as string | undefined;
  const trimmed = v?.trim();
  return trimmed ? trimmed : undefined;
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
