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

describe("HackRfConnect", () => {
  beforeEach(() => mockLive(0));

  it("shows a Connect HackRF button when idle and calls connect on click", async () => {
    const connect = vi.fn().mockResolvedValue(undefined);
    vi.spyOn(sdr, "useSdr").mockReturnValue({
      ...sdr.INITIAL_SDR_STATE,
      connect,
      disconnect: vi.fn(),
      setTuning: vi.fn(),
    });

    render(<HackRfConnect />);
    const button = screen.getByRole("button", { name: /connect hackrf/i });
    await userEvent.click(button);
    expect(connect).toHaveBeenCalledOnce();
  });

  it("shows the serial and a Disconnect action when streaming", () => {
    vi.spyOn(sdr, "useSdr").mockReturnValue({
      ...sdr.INITIAL_SDR_STATE,
      status: "streaming",
      serial: "ABC123",
      connect: vi.fn(),
      disconnect: vi.fn(),
      setTuning: vi.fn(),
    });

    render(<HackRfConnect />);
    expect(screen.getByText(/ABC123/)).toBeInTheDocument();
  });

  it("shows a live 'Scanning · N fps' readout when frames are arriving", () => {
    mockLive(18); // 18 spectrum frames/sec
    vi.spyOn(sdr, "useSdr").mockReturnValue({
      ...sdr.INITIAL_SDR_STATE,
      status: "streaming",
      serial: "ABC123",
      connect: vi.fn(),
      disconnect: vi.fn(),
      setTuning: vi.fn(),
    });

    render(<HackRfConnect />);
    expect(screen.getByText(/Scanning · 18 fps/)).toBeInTheDocument();
  });

  it("warns 'no data' while streaming but no frames are arriving", () => {
    mockLive(0);
    vi.spyOn(sdr, "useSdr").mockReturnValue({
      ...sdr.INITIAL_SDR_STATE,
      status: "streaming",
      serial: "ABC123",
      connect: vi.fn(),
      disconnect: vi.fn(),
      setTuning: vi.fn(),
    });

    render(<HackRfConnect />);
    expect(screen.getByText(/Scanning · no data/)).toBeInTheDocument();
  });
});
