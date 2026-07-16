// Spectrum helpers: sequential-blue power-ramp interpolation, frequency
// formatting, and priority-band definitions. Pure functions — no React.

import { powerRamp } from "../theme/palette";
import type { SpectrumFrame } from "../api";

type Rgb = [number, number, number];

function hexToRgb(hex: string): Rgb {
  const h = hex.replace("#", "");
  return [
    parseInt(h.slice(0, 2), 16),
    parseInt(h.slice(2, 4), 16),
    parseInt(h.slice(4, 6), 16),
  ];
}

const rampRgb: Rgb[] = powerRamp.map(hexToRgb);

/** Clamp `v` into [0, 1]. */
function clamp01(v: number): number {
  return v < 0 ? 0 : v > 1 ? 1 : v;
}

/**
 * Map t∈[0,1] to an interpolated RGB across the SEQUENTIAL BLUE power ramp
 * (light `#cde2fb` = low power → dark `#0d366b` = high power). Not a rainbow.
 */
export function powerRampRgb(t: number): Rgb {
  const c = clamp01(t);
  const seg = c * (rampRgb.length - 1);
  const i = Math.floor(seg);
  const f = seg - i;
  const a = rampRgb[i];
  const b = rampRgb[Math.min(i + 1, rampRgb.length - 1)];
  return [a[0] + (b[0] - a[0]) * f, a[1] + (b[1] - a[1]) * f, a[2] + (b[2] - a[2]) * f];
}

/** CSS `rgb()` string for a normalized power value. */
export function powerRampCss(t: number): string {
  const [r, g, b] = powerRampRgb(t);
  return `rgb(${Math.round(r)}, ${Math.round(g)}, ${Math.round(b)})`;
}

/** Vertical gradient (low at bottom → high at top) for the power-scale legend. */
export const powerGradientCss = `linear-gradient(to top, ${powerRamp.join(", ")})`;

/** Normalize `v` into [0,1] against [min,max]; 0 when the range is degenerate. */
export function normalize(v: number, min: number, max: number): number {
  if (max <= min) return 0;
  return (v - min) / (max - min);
}

/** Global dBFS min/max across all frame bins (drives the waterfall + legend). */
export function powerRange(frames: SpectrumFrame[]): { min: number; max: number } {
  let min = Infinity;
  let max = -Infinity;
  for (const f of frames) {
    for (const p of f.powerDbfs) {
      if (p < min) min = p;
      if (p > max) max = p;
    }
  }
  if (!isFinite(min) || !isFinite(max)) return { min: -120, max: 0 };
  return { min, max };
}

/** Hz → MHz numeric. */
export function toMhz(hz: number): number {
  return hz / 1e6;
}

/** Hz → fixed-precision MHz string. */
export function formatMhz(hz: number, digits = 1): string {
  return (hz / 1e6).toFixed(digits);
}

/** Frequency span [start, stop] of a frame from its center + sample rate. */
export function frameSpan(frame: SpectrumFrame): { startHz: number; stopHz: number } {
  const half = frame.sampleRateHz / 2;
  return { startHz: frame.centerFreqHz - half, stopHz: frame.centerFreqHz + half };
}

export interface PriorityBand {
  key: string;
  label: string;
  startHz: number;
  stopHz: number;
}

/** Priority bands for the coverage strip (SPEC monitored ranges). */
export const priorityBands: PriorityBand[] = [
  { key: "ads-b", label: "ADS-B", startHz: 1.08e9, stopHz: 1.09e9 },
  { key: "wifi", label: "Wi-Fi / BLE / Zigbee", startHz: 2.4e9, stopHz: 2.4835e9 },
  { key: "ism", label: "ISM 900", startHz: 9.02e8, stopHz: 9.28e8 },
  { key: "fm", label: "FM", startHz: 8.8e7, stopHz: 1.08e8 },
];
