import { describe, it, expect } from "vitest";
import { bandPresets, activeBand, isNoaaAptBand, isMappingBand } from "./bandPresets";

const KNOWN_KEYS = [
  "fm-broadcast",
  "adsb-1090",
  "ism-2400",
  "ism-sub-ghz",
  "airband",
  "marine-vhf",
  "noaa-apt",
  "noaa-weather",
  "amateur-2m",
  "amateur-70cm",
  "gps-l1",
  "dab-band3",
  "wifi-5ghz",
];

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

  it("keeps every band and channel frequency within the HackRF 1 MHz-6 GHz tuning range", () => {
    for (const b of bandPresets) {
      expect(b.centerFreqHz).toBeGreaterThanOrEqual(1_000_000);
      expect(b.centerFreqHz).toBeLessThanOrEqual(6_000_000_000);
      for (const c of b.channels) {
        expect(c.centerFreqHz, `${b.key}/${c.key}`).toBeGreaterThanOrEqual(1_000_000);
        expect(c.centerFreqHz, `${b.key}/${c.key}`).toBeLessThanOrEqual(6_000_000_000);
      }
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

  it("prefers the narrowest matching band when windows overlap (70cm inside sub-GHz ISM)", () => {
    // amateur-70cm (430-440 MHz) sits entirely inside ism-sub-ghz (433.05-928 MHz); the
    // narrower, more specific 70cm band must win, not the first catalog match.
    expect(activeBand(435_000_000)?.key).toBe("amateur-70cm");
  });

  it("still resolves a non-overlapping band correctly (regression guard)", () => {
    expect(activeBand(98_000_000)?.key).toBe("fm-broadcast");
  });

  it("resolves the NOAA APT satellite band at 137.1 MHz", () => {
    expect(activeBand(137_100_000)?.key).toBe("noaa-apt");
  });
});

describe("isNoaaAptBand", () => {
  it("is true within [137, 138] MHz inclusive", () => {
    expect(isNoaaAptBand(137_000_000)).toBe(true);
    expect(isNoaaAptBand(137_100_000)).toBe(true);
    expect(isNoaaAptBand(138_000_000)).toBe(true);
  });

  it("is false just outside the window, for unrelated bands, and for null", () => {
    expect(isNoaaAptBand(136_999_999)).toBe(false);
    expect(isNoaaAptBand(138_000_001)).toBe(false);
    expect(isNoaaAptBand(1_090_000_000)).toBe(false);
    expect(isNoaaAptBand(null)).toBe(false);
  });
});

describe("isMappingBand", () => {
  it("is true within the ADS-B 1090 MHz window", () => {
    expect(isMappingBand(1_087_000_000)).toBe(true);
    expect(isMappingBand(1_090_000_000)).toBe(true);
    expect(isMappingBand(1_093_000_000)).toBe(true);
  });

  it("is true within the NOAA APT window", () => {
    expect(isMappingBand(137_100_000)).toBe(true);
  });

  it("is false outside both windows and for null", () => {
    expect(isMappingBand(1_086_999_999)).toBe(false);
    expect(isMappingBand(1_093_000_001)).toBe(false);
    expect(isMappingBand(136_999_999)).toBe(false);
    expect(isMappingBand(138_000_001)).toBe(false);
    expect(isMappingBand(98_000_000)).toBe(false);
    expect(isMappingBand(null)).toBe(false);
  });
});
