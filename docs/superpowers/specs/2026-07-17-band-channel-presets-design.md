# Band / Channel Presets ("Frequency Changer") — Design

**Date:** 2026-07-17
**Status:** Approved for planning
**Scope:** Frontend only (`web/`). No backend, API, or DTO changes.

## Summary

Add a **band/channel preset catalog** ("frequency changer") to the Signal Atlas web UI,
surfaced in two places that drive the same HackRF retune:

1. A **dropdown band/channel menu** in the navbar (visible only while a HackRF is streaming),
   next to the existing "Scanning" chip and ⚙ tuning icon.
2. **Actionable coverage rows** on the Live Spectrum page: each coverage band gains a "Tune"
   action (band-center default) with expandable per-channel chips.

Both surfaces read one static catalog and call the existing
`sdr.setTuning({ centerFreqHz, sampleRateHz })` — no new state, no new API.

## Motivation

Tuning today is a raw "Center (MHz)" number box buried in the tuning popover. There is no
quick way to jump to a band of interest, and the coverage strip (which already names the four
priority bands and their windows) is purely informational. A curated preset list turns both
into one-click "swap to this coverage" actions.

## Why frontend-only (chosen approach)

Tuning is **entirely a browser/WebUSB concern** — the backend is a receive-only IQ sink
(Prime Invariant L1) and never commands the radio. The band `key`s already exist on both sides
(`SpectrumSupport.Bands` ↔ `web/src/lib/spectrum.ts priorityBands` ↔ the `/spectrum/coverage`
payload), so a coverage row joins to its preset by that key. Keeping presets in the frontend
avoids adding DTO fields the backend has no functional use for — i.e. it does not open a new
front/back type-drift surface (CLAUDE.md landmine #1). A mismatched/absent key degrades
gracefully (the row stays informational), never crashes.

Rejected alternatives:
- **Backend-served presets** — adds `/spectrum/coverage` DTO fields for static reference data
  the receive-only backend never uses; extra drift risk for no benefit.
- **Popover-only, no navbar dropdown** — minimal, but the dropdown was explicitly chosen.

## Architecture & data model

New single-source catalog: `web/src/sdr/bandPresets.ts`.

```ts
export interface ChannelPreset {
  key: string;          // "wifi-ch6"
  label: string;        // "Ch 6"
  centerFreqHz: number; // 2_437_000_000
  sampleRateHz: number; // 2_000_000
}

export interface BandPreset {
  key: string;          // "ism-2400" — MATCHES the /spectrum/coverage band key (the join)
  label: string;        // "Wi-Fi / BLE / Zigbee 2.4 GHz"
  lowHz: number;        // band window, for active-band detection
  highHz: number;
  centerFreqHz: number; // band-center default tune
  sampleRateHz: number; // survey rate for the band default (HackRF-valid, capped)
  channels: ChannelPreset[];
}

export const bandPresets: BandPreset[];

/** The band whose [lowHz, highHz] window contains centerHz, else null (=> "Custom"). */
export function activeBand(centerHz: number): BandPreset | null;
```

**Catalog contents** (band keys mirror `SpectrumSupport.Bands`):

| key | label | window | center default | channels |
|-----|-------|--------|----------------|----------|
| `fm-broadcast` | FM broadcast 88–108 MHz | 88–108 MHz | 98.0 MHz | a few representative stations (e.g. 90.1, 98.0, 104.5 MHz) |
| `adsb-1090` | ADS-B 1090 MHz | 1087–1093 MHz | 1090.0 MHz | 1090 (single channel) |
| `ism-2400` | Wi-Fi / BLE / Zigbee 2.4 GHz | 2400–2485 MHz | 2442 MHz | Wi-Fi ch 1 (2412), ch 6 (2437), ch 11 (2462) |
| `ism-sub-ghz` | ISM 902–928 & 433 MHz | 433.05–928 MHz | 915 MHz | 433.92 MHz, 915 MHz |

Sample rates are chosen per preset and are valid HackRF rates (2–20 MS/s); the band-center
defaults may use a wider survey rate, channels default to 2 MS/s. Exact station/channel picks
are finalized during implementation but stay within each band window.

## Components

### `web/src/components/BandSelector.tsx` (new)
- Rendered inside `HackRfConnect`'s **streaming** branch only, between the Scanning chip and the
  ⚙ tuning `IconButton`.
- Trigger: an MUI `Button`/`Chip` labeled `activeBand(sdr.tuning.centerFreqHz)?.label ?? "Custom"`
  with a ▾ affordance.
- Opens an MUI `Menu`. Each band renders as a header item (click → tune to band center) with its
  channels as indented items below (`Ch 6 · 2437 MHz`). The item whose center matches the current
  tuning gets a ✓.
- Every item `onClick` → `void sdr.setTuning({ centerFreqHz, sampleRateHz })`, then close the menu.

### `web/src/components/CoverageStrip.tsx` (modify)
- Add `useSdr()`. For each row, look up `bandPresets` by `band.key`.
- When a preset exists **and** `sdr.status === "streaming"`: render a right-aligned **"Tune"**
  button (tunes to the band center) and, if the band has channels, an expand toggle revealing
  channel chips that each tune on click.
- Otherwise (not streaming, or no matching preset): no action — rows stay informational exactly as
  today, so seed/offline data pages are unaffected.

## Data flow

```
user clicks band/channel  (BandSelector menu OR Coverage "Tune")
  └─ sdr.setTuning({ centerFreqHz, sampleRateHz })          // existing SdrProvider action
       ├─ updates tuningRef + dispatches "tuning"
       └─ if device connected: applyTuningToDevice() + iq.sendConfig()   // live retune
  └─ navbar "Scanning · N fps · <MHz>" chip + Waterfall ticks reflect the new center on next frame
```

No new selection state: the active-band label and coverage ✓/highlight derive purely from
`sdr.tuning.centerFreqHz` via `activeBand()`. Selecting is fire-and-forget (`void`), matching how
the existing popover fields already call `setTuning`.

## Edge cases

- **Not streaming** → no tune actions rendered anywhere; offline/seed pages unaffected.
- **Coverage key with no preset** → row stays informational (no crash, no button).
- **Tuned off-catalog** → navbar shows "Custom"; no coverage ✓.
- **Sample-rate validity** → guaranteed by the catalog values (HackRF-valid, capped); no runtime
  validation path needed.

## Testing (Vitest + Testing Library)

- `web/src/sdr/bandPresets.test.ts`
  - `activeBand()` returns the correct band for in-window centers, `null` for out-of-window/custom.
  - every channel center lies within its band `[lowHz, highHz]` window.
  - all `sampleRateHz` values are HackRF-valid (2–20 MS/s).
  - band keys match the known coverage keys (`fm-broadcast`, `adsb-1090`, `ism-2400`, `ism-sub-ghz`).
- `web/src/components/BandSelector.test.tsx`
  - renders the active-band label from current tuning; renders "Custom" when off-catalog.
  - clicking a channel calls `setTuning` with that channel's center + rate.
- `web/src/components/CoverageStrip.test.tsx`
  - "Tune" appears only when streaming AND a preset exists; absent when idle or no preset.
  - clicking "Tune" calls `setTuning` with the band center.

## Determinism / invariants

All changes are browser UI. Nothing touches the `src/` deterministic core, so no `IClock`/seeded-RNG
concerns (Prime Invariant P5). Receive-only (L1) is untouched — this only changes RX tuning, never
adds a transmit path. No new `/api/v1` routes.

## Out of scope (YAGNI)

- User-editable / persisted custom presets.
- Backend awareness of presets.
- Sweeping/scanning across a band automatically (single-center tuning only).
- Pre-connect band arming (chosen: controls appear only while streaming).
