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
    mockSdr(1_500_000_000);
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
