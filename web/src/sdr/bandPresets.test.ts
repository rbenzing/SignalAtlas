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
    expect(activeBand(1_500_000_000)).toBeNull();
  });
});
