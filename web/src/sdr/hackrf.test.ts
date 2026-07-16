import { describe, it, expect } from "vitest";
import { HackRfDevice, computeBasebandFilterBw, HACKRF_FILTERS } from "./hackrf";

describe("HackRfDevice — receive-only", () => {
  it("exposes no transmit/tx/send member (invariant #1)", () => {
    const names = Object.getOwnPropertyNames(HackRfDevice.prototype).map((n) => n.toLowerCase());
    const forbidden = names.filter((n) => /transmit|tx|send/.test(n));
    expect(forbidden).toEqual([]);
  });

  it("advertises the HackRF vendor/product filters", () => {
    expect(HACKRF_FILTERS).toContainEqual({ vendorId: 0x1d50, productId: 0x6089 });
  });

  it("rounds a requested baseband bandwidth down to a valid MAX2837 value", () => {
    expect(computeBasebandFilterBw(2_200_000)).toBe(1_750_000);
    expect(computeBasebandFilterBw(2_500_000)).toBe(2_500_000);
    expect(computeBasebandFilterBw(999_999_999)).toBe(28_000_000);
  });
});
