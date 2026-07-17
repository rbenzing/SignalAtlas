# Band / Channel Presets ("Frequency Changer") Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a band/channel preset catalog surfaced as a navbar dropdown and clickable coverage rows, both driving the existing HackRF retune.

**Architecture:** One static frontend catalog (`web/src/sdr/bandPresets.ts`) is the single source of truth. A new `BandSelector` navbar dropdown (streaming only) and modified `CoverageStrip` rows both read it and call the existing `sdr.setTuning({ centerFreqHz, sampleRateHz })`. No backend, API, or DTO change.

**Tech Stack:** React + TypeScript, MUI (`Menu`, `MenuItem`, `Button`, `Chip`), Vitest + `@testing-library/react` + `userEvent`.

## Global Constraints

- **Frontend only.** No changes under `src/`, no API/DTO changes, no new `/api/v1` routes.
- **Receive-only (L1).** Changes affect RX tuning only; never add a transmit path.
- **No determinism concerns.** All code is browser UI; the `src/` deterministic core is untouched — do not import `IClock`/seeded-RNG anything.
- **Frontend/API type drift (landmine #1).** Do not hand-edit `web/src/api.ts` types here. The only cross-boundary coupling is the band `key` string matching the `/spectrum/coverage` band keys — a mismatch must degrade gracefully (no action rendered), never crash.
- **Band keys are fixed:** `fm-broadcast`, `adsb-1090`, `ism-2400`, `ism-sub-ghz` (mirror `src/SignalAtlas.Api/SpectrumSupport.cs` `Bands`).
- **Verification gate per task:** run `npm run test` and `npm run build` from `web/` (Git-Bash: `cd /c/GIT/SignalAtlas/web && ...`). There is no separate lint script; `tsc -b` inside `build` is the type/lint gate.
- **Sample rates must be HackRF-valid:** integer Hz in the 2–20 MS/s range (2_000_000 … 20_000_000).

## File Structure

- **Create** `web/src/sdr/bandPresets.ts` — the catalog (`ChannelPreset`, `BandPreset`, `bandPresets`, `activeBand()`). Pure data + one pure function, no React.
- **Create** `web/src/sdr/bandPresets.test.ts` — catalog invariants + `activeBand()`.
- **Create** `web/src/components/BandSelector.tsx` — navbar dropdown menu.
- **Create** `web/src/components/BandSelector.test.tsx` — render + click-to-tune.
- **Modify** `web/src/components/HackRfConnect.tsx` — mount `<BandSelector />` in the streaming branch.
- **Modify** `web/src/components/CoverageStrip.tsx` — add streaming-gated "Tune" actions.
- **Modify** `web/src/components/CoverageStrip.test.tsx` — create if absent; test the new actions.

---

### Task 1: Band/channel catalog (`bandPresets.ts`)

**Files:**
- Create: `web/src/sdr/bandPresets.ts`
- Test: `web/src/sdr/bandPresets.test.ts`

**Interfaces:**
- Consumes: nothing (leaf module).
- Produces:
  - `interface ChannelPreset { key: string; label: string; centerFreqHz: number; sampleRateHz: number }`
  - `interface BandPreset { key: string; label: string; lowHz: number; highHz: number; centerFreqHz: number; sampleRateHz: number; channels: ChannelPreset[] }`
  - `const bandPresets: BandPreset[]`
  - `function activeBand(centerHz: number): BandPreset | null` — first band whose `[lowHz, highHz]` inclusive window contains `centerHz`, else `null`.

- [ ] **Step 1: Write the failing test**

Create `web/src/sdr/bandPresets.test.ts`:

```ts
import { describe, it, expect } from "vitest";
import { bandPresets, activeBand } from "./bandPresets";

const KNOWN_KEYS = ["fm-broadcast", "adsb-1090", "ism-2400", "ism-sub-ghz"];

describe("bandPresets catalog", () => {
  it("uses exactly the known coverage band keys", () => {
    expect(bandPresets.map((b) => b.key).sort()).toEqual([...KNOWN_KEYS].sort());
  });

  it("keeps every band center and every channel center inside the band window", () => {
    for (const b of bandPresets) {
      expect(b.centerFreqHz).toBeGreaterThanOrEqual(b.lowHz);
      expect(b.centerFreqHz).toBeLessThanOrEqual(b.highHz);
      for (const c of b.channels) {
        expect(c.centerFreqHz, `${b.key}/${c.key}`).toBeGreaterThanOrEqual(b.lowHz);
        expect(c.centerFreqHz, `${b.key}/${c.key}`).toBeLessThanOrEqual(b.highHz);
      }
    }
  });

  it("uses only HackRF-valid sample rates (2-20 MS/s, integer Hz)", () => {
    const rates = bandPresets.flatMap((b) => [b.sampleRateHz, ...b.channels.map((c) => c.sampleRateHz)]);
    for (const r of rates) {
      expect(Number.isInteger(r)).toBe(true);
      expect(r).toBeGreaterThanOrEqual(2_000_000);
      expect(r).toBeLessThanOrEqual(20_000_000);
    }
  });

  it("has unique band keys and unique channel keys within a band", () => {
    expect(new Set(bandPresets.map((b) => b.key)).size).toBe(bandPresets.length);
    for (const b of bandPresets) {
      expect(new Set(b.channels.map((c) => c.key)).size).toBe(b.channels.length);
    }
  });
});

describe("activeBand", () => {
  it("returns the band whose window contains the center", () => {
    expect(activeBand(2_437_000_000)?.key).toBe("ism-2400");
    expect(activeBand(1_090_000_000)?.key).toBe("adsb-1090");
  });

  it("returns null for an off-catalog (custom) center", () => {
    expect(activeBand(700_000_000)).toBeNull();
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd /c/GIT/SignalAtlas/web && npx vitest run src/sdr/bandPresets.test.ts`
Expected: FAIL — cannot resolve `./bandPresets`.

- [ ] **Step 3: Write minimal implementation**

Create `web/src/sdr/bandPresets.ts`:

```ts
// Static band/channel preset catalog for the "frequency changer". Single source of
// truth for both the navbar BandSelector and the clickable coverage rows. Pure data +
// one pure function — no React, no backend. Band keys mirror the /spectrum/coverage
// band keys (src/SignalAtlas.Api/SpectrumSupport.cs Bands) so a coverage row joins to
// its preset by key; a mismatch simply renders no tune action (never crashes).

export interface ChannelPreset {
  key: string;
  label: string;
  centerFreqHz: number;
  sampleRateHz: number;
}

export interface BandPreset {
  key: string;
  label: string;
  lowHz: number;
  highHz: number;
  centerFreqHz: number;
  sampleRateHz: number;
  channels: ChannelPreset[];
}

const MS = 1_000_000;

export const bandPresets: BandPreset[] = [
  {
    key: "fm-broadcast",
    label: "FM broadcast 88-108 MHz",
    lowHz: 88 * MS,
    highHz: 108 * MS,
    centerFreqHz: 98 * MS,
    sampleRateHz: 10 * MS,
    channels: [
      { key: "fm-901", label: "90.1 MHz", centerFreqHz: 90_100_000, sampleRateHz: 2 * MS },
      { key: "fm-980", label: "98.0 MHz", centerFreqHz: 98_000_000, sampleRateHz: 2 * MS },
      { key: "fm-1045", label: "104.5 MHz", centerFreqHz: 104_500_000, sampleRateHz: 2 * MS },
    ],
  },
  {
    key: "adsb-1090",
    label: "ADS-B 1090 MHz",
    lowHz: 1_087_000_000,
    highHz: 1_093_000_000,
    centerFreqHz: 1_090_000_000,
    sampleRateHz: 2 * MS,
    channels: [
      { key: "adsb-1090", label: "1090 MHz", centerFreqHz: 1_090_000_000, sampleRateHz: 2 * MS },
    ],
  },
  {
    key: "ism-2400",
    label: "Wi-Fi / BLE / Zigbee 2.4 GHz",
    lowHz: 2_400_000_000,
    highHz: 2_485_000_000,
    centerFreqHz: 2_442_000_000,
    sampleRateHz: 20 * MS,
    channels: [
      { key: "wifi-ch1", label: "Wi-Fi ch 1 - 2412 MHz", centerFreqHz: 2_412_000_000, sampleRateHz: 20 * MS },
      { key: "wifi-ch6", label: "Wi-Fi ch 6 - 2437 MHz", centerFreqHz: 2_437_000_000, sampleRateHz: 20 * MS },
      { key: "wifi-ch11", label: "Wi-Fi ch 11 - 2462 MHz", centerFreqHz: 2_462_000_000, sampleRateHz: 20 * MS },
    ],
  },
  {
    key: "ism-sub-ghz",
    label: "ISM 902-928 & 433 MHz",
    lowHz: 433_050_000,
    highHz: 928_000_000,
    centerFreqHz: 915_000_000,
    sampleRateHz: 2 * MS,
    channels: [
      { key: "ism-433", label: "433.92 MHz", centerFreqHz: 433_920_000, sampleRateHz: 2 * MS },
      { key: "ism-915", label: "915 MHz", centerFreqHz: 915_000_000, sampleRateHz: 2 * MS },
    ],
  },
];

/** The band whose inclusive [lowHz, highHz] window contains centerHz, else null (=> "Custom"). */
export function activeBand(centerHz: number): BandPreset | null {
  return bandPresets.find((b) => centerHz >= b.lowHz && centerHz <= b.highHz) ?? null;
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd /c/GIT/SignalAtlas/web && npx vitest run src/sdr/bandPresets.test.ts`
Expected: PASS (all cases).

- [ ] **Step 5: Commit**

```bash
cd /c/GIT/SignalAtlas && git add web/src/sdr/bandPresets.ts web/src/sdr/bandPresets.test.ts
git commit -m "feat(web): band/channel preset catalog + activeBand()

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 2: Navbar `BandSelector` dropdown + mount in `HackRfConnect`

**Files:**
- Create: `web/src/components/BandSelector.tsx`
- Test: `web/src/components/BandSelector.test.tsx`
- Modify: `web/src/components/HackRfConnect.tsx` (streaming branch: add `<BandSelector />` between the Scanning `Tooltip`/`Chip` and the Tuning `IconButton`)

**Interfaces:**
- Consumes: `bandPresets`, `activeBand` from `../sdr/bandPresets`; `useSdr` from `../sdr/SdrProvider` (provides `tuning.centerFreqHz` and `setTuning(patch)`).
- Produces: default export `BandSelector` (no props).

- [ ] **Step 1: Write the failing test**

Create `web/src/components/BandSelector.test.tsx`:

```tsx
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import BandSelector from "./BandSelector";
import * as sdr from "../sdr/SdrProvider";

function mockSdr(centerFreqHz: number) {
  const setTuning = vi.fn().mockResolvedValue(undefined);
  vi.spyOn(sdr, "useSdr").mockReturnValue({
    ...sdr.INITIAL_SDR_STATE,
    status: "streaming",
    serial: "ABC123",
    tuning: { ...sdr.INITIAL_SDR_STATE.tuning, centerFreqHz },
    connect: vi.fn(),
    disconnect: vi.fn(),
    setTuning,
  });
  return setTuning;
}

describe("BandSelector", () => {
  beforeEach(() => vi.restoreAllMocks());

  it("labels the trigger with the active band for the current center", () => {
    mockSdr(2_437_000_000); // inside ism-2400
    render(<BandSelector />);
    expect(screen.getByRole("button", { name: /Wi-Fi \/ BLE \/ Zigbee 2\.4 GHz/ })).toBeInTheDocument();
  });

  it("labels the trigger 'Custom' when tuned off-catalog", () => {
    mockSdr(700_000_000);
    render(<BandSelector />);
    expect(screen.getByRole("button", { name: /Custom/ })).toBeInTheDocument();
  });

  it("tunes to a channel's center and rate when its menu item is clicked", async () => {
    const setTuning = mockSdr(2_437_000_000);
    render(<BandSelector />);
    await userEvent.click(screen.getByRole("button", { name: /Wi-Fi/ }));
    await userEvent.click(screen.getByRole("menuitem", { name: /Wi-Fi ch 1 - 2412 MHz/ }));
    expect(setTuning).toHaveBeenCalledWith({ centerFreqHz: 2_412_000_000, sampleRateHz: 20_000_000 });
  });

  it("tunes to the band center when a band header item is clicked", async () => {
    const setTuning = mockSdr(915_000_000);
    render(<BandSelector />);
    await userEvent.click(screen.getByRole("button", { name: /ISM 902-928/ }));
    await userEvent.click(screen.getByRole("menuitem", { name: /ADS-B 1090 MHz/ }));
    expect(setTuning).toHaveBeenCalledWith({ centerFreqHz: 1_090_000_000, sampleRateHz: 2_000_000 });
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd /c/GIT/SignalAtlas/web && npx vitest run src/components/BandSelector.test.tsx`
Expected: FAIL — cannot resolve `./BandSelector`.

- [ ] **Step 3: Write minimal implementation**

Create `web/src/components/BandSelector.tsx`:

```tsx
import { useState } from "react";
import Button from "@mui/material/Button";
import Menu from "@mui/material/Menu";
import MenuItem from "@mui/material/MenuItem";
import ListItemText from "@mui/material/ListItemText";
import ListItemIcon from "@mui/material/ListItemIcon";
import CheckIcon from "@mui/icons-material/Check";
import ArrowDropDownIcon from "@mui/icons-material/ArrowDropDown";
import { useSdr } from "../sdr/SdrProvider";
import { bandPresets, activeBand } from "../sdr/bandPresets";

// Navbar "frequency changer": a dropdown of bands, each with its channels indented
// beneath a band-center header. Every item retunes via the existing setTuning — the
// active label/checkmark derive purely from the current tuning center (no extra state).
export default function BandSelector() {
  const sdr = useSdr();
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);
  const center = sdr.tuning.centerFreqHz;
  const current = activeBand(center);

  const tune = (centerFreqHz: number, sampleRateHz: number) => {
    void sdr.setTuning({ centerFreqHz, sampleRateHz });
    setAnchor(null);
  };

  return (
    <>
      <Button
        size="small"
        variant="outlined"
        color="inherit"
        endIcon={<ArrowDropDownIcon />}
        onClick={(e) => setAnchor(e.currentTarget)}
      >
        {current?.label ?? "Custom"}
      </Button>
      <Menu anchorEl={anchor} open={Boolean(anchor)} onClose={() => setAnchor(null)}>
        {bandPresets.flatMap((band) => [
          <MenuItem
            key={band.key}
            onClick={() => tune(band.centerFreqHz, band.sampleRateHz)}
            sx={{ fontWeight: 600 }}
          >
            <ListItemIcon>{center === band.centerFreqHz ? <CheckIcon fontSize="small" /> : null}</ListItemIcon>
            <ListItemText>{band.label}</ListItemText>
          </MenuItem>,
          ...band.channels.map((ch) => (
            <MenuItem
              key={`${band.key}/${ch.key}`}
              onClick={() => tune(ch.centerFreqHz, ch.sampleRateHz)}
              sx={{ pl: 4 }}
            >
              <ListItemIcon>{center === ch.centerFreqHz ? <CheckIcon fontSize="small" /> : null}</ListItemIcon>
              <ListItemText>{ch.label}</ListItemText>
            </MenuItem>
          )),
        ])}
      </Menu>
    </>
  );
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd /c/GIT/SignalAtlas/web && npx vitest run src/components/BandSelector.test.tsx`
Expected: PASS.

> Note: the band-header `MenuItem` renders its `label`; the ADS-B header text is `ADS-B 1090 MHz`. The channel item text `Wi-Fi ch 1 - 2412 MHz` matches the catalog label. If `getByRole("menuitem", ...)` matches multiple nodes (a band label that is a substring of a channel label), tighten the regex with anchors in the test — current labels are unambiguous.

- [ ] **Step 5: Mount in `HackRfConnect`**

In `web/src/components/HackRfConnect.tsx`, add the import near the other imports:

```tsx
import BandSelector from "./BandSelector";
```

Then in the streaming `return (...)`, insert `<BandSelector />` between the Scanning `Tooltip` block (the one ending `</Tooltip>` after the `Scanning · ...` Chip) and the `Tooltip title="Tuning"` block. Concretely, place it immediately before:

```tsx
      <Tooltip title="Tuning">
```

so the row reads: HackRF chip → Scanning chip → **BandSelector** → Tuning gear → Disconnect.

- [ ] **Step 6: Run the full web test suite + build**

Run: `cd /c/GIT/SignalAtlas/web && npm run test && npm run build`
Expected: all tests PASS; `tsc -b && vite build` completes with no type errors.

- [ ] **Step 7: Commit**

```bash
cd /c/GIT/SignalAtlas && git add web/src/components/BandSelector.tsx web/src/components/BandSelector.test.tsx web/src/components/HackRfConnect.tsx
git commit -m "feat(web): navbar band/channel dropdown (frequency changer)

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

### Task 3: Actionable coverage rows (`CoverageStrip`)

**Files:**
- Modify: `web/src/components/CoverageStrip.tsx`
- Test: `web/src/components/CoverageStrip.test.tsx` (create if absent)

**Interfaces:**
- Consumes: `bandPresets` from `../sdr/bandPresets`; `useSdr` from `../sdr/SdrProvider`; existing `SpectrumCoverageBand` prop type (has `key`, `label`, `lowHz`, `highHz`, `covered`, `ageSeconds`).
- Produces: no new exports — same default `CoverageStrip` component, now with a streaming-gated "Tune" action per row that has a matching preset.

- [ ] **Step 1: Write the failing test**

Create `web/src/components/CoverageStrip.test.tsx`:

```tsx
import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import CoverageStrip from "./CoverageStrip";
import * as sdr from "../sdr/SdrProvider";
import type { SpectrumCoverageBand } from "../api";

const WIFI: SpectrumCoverageBand = {
  key: "ism-2400",
  label: "Wi-Fi / BLE / Zigbee 2.4 GHz",
  lowHz: 2_400_000_000,
  highHz: 2_485_000_000,
  lastSeen: null,
  ageSeconds: null,
  covered: false,
};

function mockSdr(status: sdr.SdrStatus) {
  const setTuning = vi.fn().mockResolvedValue(undefined);
  vi.spyOn(sdr, "useSdr").mockReturnValue({
    ...sdr.INITIAL_SDR_STATE,
    status,
    connect: vi.fn(),
    disconnect: vi.fn(),
    setTuning,
  });
  return setTuning;
}

describe("CoverageStrip tune actions", () => {
  beforeEach(() => vi.restoreAllMocks());

  it("shows a Tune action for a band with a preset while streaming", () => {
    mockSdr("streaming");
    render(<CoverageStrip bands={[WIFI]} />);
    expect(screen.getByRole("button", { name: /tune/i })).toBeInTheDocument();
  });

  it("shows no Tune action when idle", () => {
    mockSdr("idle");
    render(<CoverageStrip bands={[WIFI]} />);
    expect(screen.queryByRole("button", { name: /tune/i })).toBeNull();
  });

  it("shows no Tune action for a band key with no preset", () => {
    mockSdr("streaming");
    render(<CoverageStrip bands={[{ ...WIFI, key: "unknown-band" }]} />);
    expect(screen.queryByRole("button", { name: /tune/i })).toBeNull();
  });

  it("tunes to the band center when Tune is clicked", async () => {
    const setTuning = mockSdr("streaming");
    render(<CoverageStrip bands={[WIFI]} />);
    await userEvent.click(screen.getByRole("button", { name: /tune/i }));
    expect(setTuning).toHaveBeenCalledWith({ centerFreqHz: 2_442_000_000, sampleRateHz: 20_000_000 });
  });
});
```

- [ ] **Step 2: Run test to verify it fails**

Run: `cd /c/GIT/SignalAtlas/web && npx vitest run src/components/CoverageStrip.test.tsx`
Expected: FAIL — no `Tune` button exists yet.

- [ ] **Step 3: Write minimal implementation**

Edit `web/src/components/CoverageStrip.tsx`. Add imports at the top:

```tsx
import Button from "@mui/material/Button";
import { useSdr } from "../sdr/SdrProvider";
import { bandPresets } from "../sdr/bandPresets";
```

Inside the component, before the `return`, read the SDR state:

```tsx
  const sdr = useSdr();
  const streaming = sdr.status === "streaming";
```

Then, inside the `bands.map((b) => { ... })` body, after computing `statusColor` and before the returned `<Box key={b.key} ...>`, look up the preset:

```tsx
        const preset = bandPresets.find((p) => p.key === b.key);
```

Finally, inside the row — replace the trailing `ml: "auto"` info `<Box>` grouping so the age block and a new Tune button sit together on the right. Change the last child block from:

```tsx
            <Box sx={{ ml: "auto", textAlign: "right" }}>
              <Typography variant="body2" sx={{ fontWeight: 600 }}>
                {meta.label}
              </Typography>
              <Typography variant="caption" color="text.secondary">
                {formatAge(b.ageSeconds)}
              </Typography>
            </Box>
```

to:

```tsx
            <Box sx={{ ml: "auto", display: "flex", alignItems: "center", gap: 1.5 }}>
              <Box sx={{ textAlign: "right" }}>
                <Typography variant="body2" sx={{ fontWeight: 600 }}>
                  {meta.label}
                </Typography>
                <Typography variant="caption" color="text.secondary">
                  {formatAge(b.ageSeconds)}
                </Typography>
              </Box>
              {streaming && preset && (
                <Button
                  size="small"
                  variant="outlined"
                  onClick={() => void sdr.setTuning({ centerFreqHz: preset.centerFreqHz, sampleRateHz: preset.sampleRateHz })}
                >
                  Tune
                </Button>
              )}
            </Box>
```

- [ ] **Step 4: Run test to verify it passes**

Run: `cd /c/GIT/SignalAtlas/web && npx vitest run src/components/CoverageStrip.test.tsx`
Expected: PASS.

- [ ] **Step 5: Run the full web test suite + build**

Run: `cd /c/GIT/SignalAtlas/web && npm run test && npm run build`
Expected: all tests PASS (including the pre-existing `HackRfConnect` / `spectrum` suites); build clean.

> Note: `CoverageStrip` now calls `useSdr()`, so any pre-existing render of `CoverageStrip` in tests must be wrapped in an `SdrProvider` or have `useSdr` mocked. The Live Spectrum view already renders inside the app providers, so runtime is fine; only direct-render tests need the mock (this task's test mocks it).

- [ ] **Step 6: Commit**

```bash
cd /c/GIT/SignalAtlas && git add web/src/components/CoverageStrip.tsx web/src/components/CoverageStrip.test.tsx
git commit -m "feat(web): clickable coverage rows tune the HackRF to a band

Co-Authored-By: Claude Opus 4.8 <noreply@anthropic.com>"
```

---

## Self-Review

**Spec coverage:**
- Catalog `bandPresets.ts` (`ChannelPreset`, `BandPreset`, `bandPresets`, `activeBand`) → Task 1. ✓
- Navbar dropdown, streaming-only, active-band label + ✓, click-to-tune band center & channels → Task 2. ✓
- Coverage rows: streaming-gated Tune, band-center tune, no action when idle / no preset → Task 3. ✓
- Data flow via existing `setTuning` (no new state) → Tasks 2 & 3 call `sdr.setTuning`. ✓
- Edge cases (not streaming, no-preset key, off-catalog "Custom", HackRF-valid rates) → covered by Task 1 & 3 tests and the `activeBand`/`streaming && preset` guards. ✓
- Testing: `bandPresets.test.ts`, `BandSelector.test.tsx`, `CoverageStrip.test.tsx` → Tasks 1–3. ✓
- Determinism / L1 / no-DTO-change constraints → Global Constraints; no `src/` or `api.ts` edits in any task. ✓
- **Deferred channel expansion in coverage rows:** the spec allows an expand toggle to reveal channel chips on coverage rows. This plan ships the band-center "Tune" button only (the required behavior); per-row channel chips are omitted as YAGNI for v1 since the navbar dropdown already exposes every channel. If desired, add as a follow-up task. **No spec requirement is left unimplemented** — the expand toggle was described as optional ("if the band has channels").

**Placeholder scan:** No TBD/TODO/"handle edge cases"/"similar to Task N". All code shown in full. ✓

**Type consistency:** `setTuning({ centerFreqHz, sampleRateHz })` shape matches `Partial<SdrTuning>` (`SdrProvider.tsx:263`). `activeBand(centerHz: number): BandPreset | null` used identically in Task 2. `SpectrumCoverageBand` fields (`key`, `label`, `lowHz`, `highHz`, `covered`, `ageSeconds`, `lastSeen`) match `web/src/api.ts:91`. Band keys identical across catalog, tests, and `SpectrumSupport.cs`. ✓
