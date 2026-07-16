// LOCKED, validated Signal Atlas palette (design review sign-off).
// Do not change hex values without a new palette validation pass.

export type ColorMode = "light" | "dark";

/** Protocol categorical colors, tuned per light/dark surface for AA contrast. */
export const protocolPalette: Record<string, Record<ColorMode, string>> = {
  "wi-fi": { light: "#2a78d6", dark: "#3987e5" },
  ble: { light: "#1baf7a", dark: "#199e70" },
  lora: { light: "#eda100", dark: "#c98500" },
  zigbee: { light: "#008300", dark: "#008300" },
  "ads-b": { light: "#4a3aa7", dark: "#9085e9" },
  fm: { light: "#e34948", dark: "#e66767" },
  unknown: { light: "#898781", dark: "#898781" },
};

/** Canonical protocol labels for legends/tables (keyed by normalized id). */
export const protocolLabels: Record<string, string> = {
  "wi-fi": "Wi-Fi",
  ble: "BLE",
  lora: "LoRa",
  zigbee: "Zigbee",
  "ads-b": "ADS-B",
  fm: "FM",
  unknown: "Unknown",
};

/** Normalize a protocol string to a palette key (case-insensitive, tolerant). */
function normalizeProtocol(protocol: string): string {
  const p = protocol.trim().toLowerCase();
  if (p === "wifi" || p === "wi-fi" || p === "802.11") return "wi-fi";
  if (p === "adsb" || p === "ads-b") return "ads-b";
  if (p === "bluetooth" || p === "ble") return "ble";
  return p;
}

/**
 * Resolve the categorical color for a protocol. Case-insensitive;
 * unknown/unlisted protocols fall back to the neutral gray.
 */
export function protocolColor(protocol: string, mode: ColorMode = "light"): string {
  const key = normalizeProtocol(protocol);
  const entry = protocolPalette[key] ?? protocolPalette.unknown;
  return entry[mode];
}

/** Ordered list of known protocols for legends. */
export const protocolOrder = ["wi-fi", "ble", "lora", "zigbee", "ads-b", "fm", "unknown"] as const;

/** Status colors (device/alert health). Mode-agnostic — chosen for both surfaces. */
export const statusPalette = {
  good: "#0ca30c",
  warning: "#fab219",
  serious: "#ec835a",
  critical: "#d03b3b",
} as const;

export type StatusKey = keyof typeof statusPalette;

/**
 * Sequential BLUE ramp for the waterfall power scale (low -> high power).
 * Steps interpolated between the locked endpoints #cde2fb -> #0d366b.
 */
export const powerRamp = [
  "#cde2fb",
  "#a9caf3",
  "#84b2ea",
  "#5e97dd",
  "#3d7bc9",
  "#2560ad",
  "#164a8c",
  "#0d366b",
] as const;

/** Chart ink / grid / surface tokens per mode. */
export const chartTokens: Record<
  ColorMode,
  { ink: string; grid: string; surface: string; surfaceAlt: string; axis: string }
> = {
  light: {
    ink: "#1a1c1e",
    grid: "#e2e5ea",
    surface: "#ffffff",
    surfaceAlt: "#f5f7fa",
    axis: "#5b6069",
  },
  dark: {
    ink: "#e6e8ec",
    grid: "#2b2f36",
    surface: "#161a20",
    surfaceAlt: "#1e232b",
    axis: "#9aa0aa",
  },
};
