import { describe, it, expect, vi, beforeEach } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import HackRfConnect from "./HackRfConnect";
import * as sdr from "../sdr/SdrProvider";
import * as live from "../live/LiveProvider";

function mockLive(spectrumFps = 0) {
  vi.spyOn(live, "useLive").mockReturnValue({
    status: "disconnected",
    signals: [],
    emitters: [],
    alerts: [],
    spectrumFrames: [],
    spectrumFps,
  });
}

function mockSdr(overrides: Partial<ReturnType<typeof sdr.useSdr>>) {
  const value = {
    ...sdr.INITIAL_SDR_STATE,
    connect: vi.fn(),
    disconnect: vi.fn(),
    tune: vi.fn(),
    stop: vi.fn(),
    setTuning: vi.fn(),
    ...overrides,
  };
  vi.spyOn(sdr, "useSdr").mockReturnValue(value);
  return value;
}

describe("HackRfConnect", () => {
  beforeEach(() => mockLive(0));

  it("shows a Connect HackRF button when idle and calls connect on click", async () => {
    const value = mockSdr({});
    render(<HackRfConnect />);
    const button = screen.getByRole("button", { name: /connect hackrf/i });
    await userEvent.click(button);
    expect(value.connect).toHaveBeenCalledOnce();
  });

  it("shows the serial and a Disconnect action when streaming", () => {
    mockSdr({ status: "streaming", serial: "ABC123", activeFreqHz: 915_000_000 });
    render(<HackRfConnect />);
    expect(screen.getByText(/ABC123/)).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /disconnect/i })).toBeInTheDocument();
  });

  it("shows an Idle readout and NO Stop button when ready (adopted, not scanning)", () => {
    mockSdr({ status: "ready", serial: "ABC123", activeFreqHz: null });
    render(<HackRfConnect />);
    expect(screen.getByText(/Idle · select a frequency/)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /^stop$/i })).toBeNull();
  });

  it("shows a Stop button when streaming and calls stop() on click", async () => {
    const value = mockSdr({ status: "streaming", serial: "ABC123", activeFreqHz: 915_000_000 });
    render(<HackRfConnect />);
    const stop = screen.getByRole("button", { name: /^stop$/i });
    await userEvent.click(stop);
    expect(value.stop).toHaveBeenCalledOnce();
  });

  it("shows a live 'Scanning · N fps' readout when frames are arriving", () => {
    mockLive(18); // 18 spectrum frames/sec
    mockSdr({ status: "streaming", serial: "ABC123", activeFreqHz: 915_000_000 });
    render(<HackRfConnect />);
    expect(screen.getByText(/Scanning · 18 fps/)).toBeInTheDocument();
  });

  it("warns 'no data' while streaming but no frames are arriving", () => {
    mockLive(0);
    mockSdr({ status: "streaming", serial: "ABC123", activeFreqHz: 915_000_000 });
    render(<HackRfConnect />);
    expect(screen.getByText(/Scanning · no data/)).toBeInTheDocument();
  });
});
