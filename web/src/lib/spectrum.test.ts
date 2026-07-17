import { describe, it, expect } from "vitest";
import { powerRange } from "./spectrum";
import type { SpectrumFrame } from "../api";

function frame(powerDbfs: number[]): SpectrumFrame {
  return { time: "2026-07-17T00:00:00Z", centerFreqHz: 915e6, sampleRateHz: 2e6, powerDbfs };
}

describe("powerRange (robust)", () => {
  it("ignores a DC-offset spike at the center bin so real signals set the ceiling", () => {
    // Noise floor ~-60, a real signal at -45, and a huge DC spike at the center bin (+40).
    const n = 100;
    const bins = new Array<number>(n).fill(-60);
    bins[20] = -45; // a real signal
    bins[n >> 1] = 40; // DC-offset spike at center — must be excluded from ranging

    const { min, max } = powerRange([frame(bins)]);

    expect(max).toBeLessThan(0); // NOT dominated by the +40 dc spike
    expect(max).toBeGreaterThanOrEqual(-46); // ceiling reflects the real -45 signal
    expect(min).toBeLessThanOrEqual(-55); // floor near the noise floor
  });

  it("does not let a narrow spike set the ceiling (99th-percentile over a full spectrum)", () => {
    const n = 4096; // realistic FFT size
    const bins = new Array<number>(n).fill(-70);
    bins[40] = 30; // one narrow, very strong non-center bin

    const { max } = powerRange([frame(bins)]);

    // The lone spike is above the 99th percentile → excluded from the ceiling (it still renders
    // clamped-bright), so the range stays near the noise floor instead of being blown out to +30.
    expect(max).toBeLessThan(-50);
  });

  it("falls back to a sane range when there is no data", () => {
    expect(powerRange([])).toEqual({ min: -100, max: 0 });
  });
});
