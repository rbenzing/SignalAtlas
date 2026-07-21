import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import BandSelector from "./BandSelector";
import * as sdr from "../sdr/SdrProvider";

// activeFreqHz drives the label/checkmark now (the frequency actually being captured), not
// tuning.centerFreqHz. Pass null to model the "ready" (adopted, not scanning) state.
function mockSdr(activeFreqHz: number | null) {
  const tune = vi.fn().mockResolvedValue(undefined);
  vi.spyOn(sdr, "useSdr").mockReturnValue({
    ...sdr.INITIAL_SDR_STATE,
    status: activeFreqHz === null ? "ready" : "streaming",
    serial: "ABC123",
    activeFreqHz,
    tuning: {
      ...sdr.INITIAL_SDR_STATE.tuning,
      centerFreqHz: activeFreqHz ?? sdr.INITIAL_SDR_STATE.tuning.centerFreqHz,
    },
    connect: vi.fn(),
    disconnect: vi.fn(),
    tune,
    stop: vi.fn(),
    setTuning: vi.fn(),
  });
  return tune;
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

  it("labels the trigger 'Select frequency' when adopted but not scanning (ready)", () => {
    mockSdr(null);
    render(<BandSelector />);
    expect(screen.getByRole("button", { name: /Select frequency/ })).toBeInTheDocument();
  });

  it("tunes to a channel's center and rate when its menu item is clicked", async () => {
    const tune = mockSdr(2_437_000_000);
    render(<BandSelector />);
    await userEvent.click(screen.getByRole("button", { name: /Wi-Fi/ }));
    await userEvent.click(screen.getByRole("menuitem", { name: /Wi-Fi ch 1 - 2412 MHz/ }));
    expect(tune).toHaveBeenCalledWith(2_412_000_000, 20_000_000);
  });

  it("tunes to the band center when a band header item is clicked", async () => {
    const tune = mockSdr(915_000_000);
    render(<BandSelector />);
    await userEvent.click(screen.getByRole("button", { name: /ISM 902-928/ }));
    await userEvent.click(screen.getByRole("menuitem", { name: /ADS-B 1090 MHz/ }));
    expect(tune).toHaveBeenCalledWith(1_090_000_000, 8_000_000);
  });

  it("marks the menu-trigger button with menu a11y attributes that toggle on open", async () => {
    mockSdr(2_437_000_000);
    render(<BandSelector />);
    const trigger = screen.getByRole("button", { name: /Wi-Fi/ });
    expect(trigger).toHaveAttribute("aria-haspopup", "menu");
    expect(trigger).toHaveAttribute("aria-expanded", "false");
    await userEvent.click(trigger);
    expect(trigger).toHaveAttribute("aria-expanded", "true");
  });

  it("shows exactly one checkmark on the channel (not also the band header) when the band center equals a channel center", async () => {
    // ism-sub-ghz band center 915 MHz coincides with the ism-915 channel center.
    mockSdr(915_000_000);
    render(<BandSelector />);
    await userEvent.click(screen.getByRole("button", { name: /ISM 902-928/ }));
    expect(screen.getAllByTestId("CheckIcon")).toHaveLength(1);
    // The ✓ belongs to the channel row, and the band header carries none.
    expect(within(screen.getByRole("menuitem", { name: /^915 MHz$/ })).getByTestId("CheckIcon")).toBeInTheDocument();
    expect(within(screen.getByRole("menuitem", { name: /ISM 902-928 & 433 MHz/ })).queryByTestId("CheckIcon")).toBeNull();
  });

  it("keeps the band-header checkmark for a band-center default that no channel shares", async () => {
    // ism-2400 band center 2442 MHz is not any Wi-Fi channel center → header keeps the ✓.
    mockSdr(2_442_000_000);
    render(<BandSelector />);
    await userEvent.click(screen.getByRole("button", { name: /Wi-Fi \/ BLE \/ Zigbee 2\.4 GHz/ }));
    expect(screen.getAllByTestId("CheckIcon")).toHaveLength(1);
    expect(
      within(screen.getByRole("menuitem", { name: /Wi-Fi \/ BLE \/ Zigbee 2\.4 GHz/ })).getByTestId("CheckIcon"),
    ).toBeInTheDocument();
  });
});
