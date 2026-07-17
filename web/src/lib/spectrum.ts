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

// Robust ranging: sample a bounded window of recent frames + stride the bins so the sort stays cheap
// at the live frame rate, and exclude the DC center bin (index N/2 ±1) — the SDR's DC-offset spike
// sits there and would otherwise blow out the color scale, hiding every real signal under it.
const RANGE_SAMPLE_FRAMES = 12;
const RANGE_BIN_STRIDE = 4;
const RANGE_MIN_SPAN_DB = 6;
const RANGE_LOW_PCT = 0.05;
const RANGE_HIGH_PCT = 0.99;

/**
 * Robust dBFS min/max for coloring the waterfall + legend. Uses percentiles over recent, DC-excluded
 * bins rather than raw global min/max, so a single DC-offset spike or one very strong emitter can't
 * collapse the visible range and hide real signal. Falls back to a sane default with no data.
 */
export function powerRange(frames: SpectrumFrame[]): { min: number; max: number } {
  const samples: number[] = [];
  const take = Math.min(frames.length, RANGE_SAMPLE_FRAMES);
  for (let fi = 0; fi < take; fi++) {
    const p = frames[fi].powerDbfs;
    const n = p.length;
    const dc = n >> 1;
    for (let i = 0; i < n; i += RANGE_BIN_STRIDE) {
      if (Math.abs(i - dc) <= 1) continue; // exclude the DC center bin (±1 guard)
      const v = p[i];
      if (Number.isFinite(v)) samples.push(v);
    }
  }
  if (samples.length === 0) return { min: -100, max: 0 };

  samples.sort((a, b) => a - b);
  const at = (q: number) =>
    samples[Math.min(samples.length - 1, Math.max(0, Math.round(q * (samples.length - 1))))];
  const min = at(RANGE_LOW_PCT);
  let max = at(RANGE_HIGH_PCT);
  if (max - min < RANGE_MIN_SPAN_DB) max = min + RANGE_MIN_SPAN_DB; // keep a usable span
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
