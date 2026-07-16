import { describe, it, expect } from "vitest";
import { sdrReducer, INITIAL_SDR_STATE, DEFAULT_TUNING } from "./SdrProvider";

describe("sdrReducer", () => {
  it("moves idle → requesting on connect/request", () => {
    const s = sdrReducer(INITIAL_SDR_STATE, { type: "requesting" });
    expect(s.status).toBe("requesting");
    expect(s.error).toBeNull();
  });

  it("records device info and enters streaming on connected", () => {
    const s = sdrReducer(INITIAL_SDR_STATE, {
      type: "connected",
      info: { boardId: 2, firmwareVersion: "2024.02.1", serialNumber: "ABC123" },
    });
    expect(s.status).toBe("streaming");
    expect(s.serial).toBe("ABC123");
    expect(s.firmware).toBe("2024.02.1");
  });

  it("captures an error message and returns to error status", () => {
    const s = sdrReducer(INITIAL_SDR_STATE, { type: "error", message: "No HackRF selected." });
    expect(s.status).toBe("error");
    expect(s.error).toBe("No HackRF selected.");
  });

  it("resets to idle on disconnect", () => {
    const streaming = sdrReducer(INITIAL_SDR_STATE, {
      type: "connected",
      info: { boardId: 2, firmwareVersion: "x", serialNumber: "S" },
    });
    const s = sdrReducer(streaming, { type: "disconnected" });
    expect(s.status).toBe("idle");
    expect(s.serial).toBeNull();
    expect(s.tuning).toEqual(DEFAULT_TUNING);
  });

  it("merges a tuning patch without changing status", () => {
    const s = sdrReducer(INITIAL_SDR_STATE, { type: "tuning", patch: { centerFreqHz: 433_920_000 } });
    expect(s.tuning.centerFreqHz).toBe(433_920_000);
    expect(s.tuning.sampleRateHz).toBe(DEFAULT_TUNING.sampleRateHz);
    expect(s.status).toBe("idle");
  });
});
