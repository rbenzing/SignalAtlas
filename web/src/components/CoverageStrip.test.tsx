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
  const tune = vi.fn().mockResolvedValue(undefined);
  vi.spyOn(sdr, "useSdr").mockReturnValue({
    ...sdr.INITIAL_SDR_STATE,
    status,
    connect: vi.fn(),
    disconnect: vi.fn(),
    tune,
    stop: vi.fn(),
    setTuning: vi.fn(),
  });
  return tune;
}

describe("CoverageStrip tune actions", () => {
  beforeEach(() => vi.restoreAllMocks());

  it("shows a Tune action for a band with a preset while streaming", () => {
    mockSdr("streaming");
    render(<CoverageStrip bands={[WIFI]} />);
    expect(screen.getByRole("button", { name: /tune/i })).toBeInTheDocument();
  });

  it("shows a Tune action when ready (adopted, not yet scanning)", () => {
    mockSdr("ready");
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
    const tune = mockSdr("streaming");
    render(<CoverageStrip bands={[WIFI]} />);
    await userEvent.click(screen.getByRole("button", { name: /tune/i }));
    expect(tune).toHaveBeenCalledWith(2_442_000_000, 20_000_000);
  });
});
