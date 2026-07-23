import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import AudioPlayer from "./AudioPlayer";
import * as sdr from "../sdr/SdrProvider";

// The mocked ./audio/AudioStreamPlayer module delegates every method to a vi.fn() held in
// `hoisted` (vi.mock factories are hoisted above imports, so the mock fns must come from
// vi.hoisted too — mirrors the HackRfConnect/SdrProvider test style).
const hoisted = vi.hoisted(() => ({
  playMock: vi.fn(),
  stopMock: vi.fn(),
  setModeMock: vi.fn(),
  setVolumeMock: vi.fn(),
}));

vi.mock("../audio/AudioStreamPlayer", () => ({
  AudioStreamPlayer: class {
    play(mode: string) {
      return hoisted.playMock(mode);
    }
    stop() {
      return hoisted.stopMock();
    }
    setMode(mode: string) {
      return hoisted.setModeMock(mode);
    }
    setVolume(v: number) {
      return hoisted.setVolumeMock(v);
    }
  },
}));

function mockSdr(activeFreqHz: number | null) {
  vi.spyOn(sdr, "useSdr").mockReturnValue({
    ...sdr.INITIAL_SDR_STATE,
    activeFreqHz,
    connect: vi.fn(),
    disconnect: vi.fn(),
    tune: vi.fn(),
    stop: vi.fn(),
    setTuning: vi.fn(),
  });
}

beforeEach(() => {
  hoisted.playMock.mockReset();
  hoisted.stopMock.mockReset();
  hoisted.setModeMock.mockReset();
  hoisted.setVolumeMock.mockReset();
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe("AudioPlayer disabled state", () => {
  it("shows the disabled hint and no controls when no frequency is tuned", () => {
    mockSdr(null);
    render(<AudioPlayer />);
    expect(screen.getByText(/tune a frequency to listen/i)).toBeInTheDocument();
    expect(screen.queryByRole("button", { name: /play/i })).toBeNull();
  });
});

describe("AudioPlayer with a tuned frequency", () => {
  it("shows the tuned frequency in MHz", () => {
    mockSdr(915_000_000);
    render(<AudioPlayer />);
    expect(screen.getByText("915.000 MHz")).toBeInTheDocument();
  });

  it("Play starts the stream player at the selected mode (default WBFM)", async () => {
    mockSdr(915_000_000);
    render(<AudioPlayer />);
    await userEvent.click(screen.getByRole("button", { name: /play/i }));
    expect(hoisted.playMock).toHaveBeenCalledWith("wbfm");
    expect(screen.getByRole("button", { name: /^stop$/i })).toBeInTheDocument();
  });

  it("Stop halts the stream player", async () => {
    mockSdr(915_000_000);
    render(<AudioPlayer />);
    await userEvent.click(screen.getByRole("button", { name: /play/i }));
    await userEvent.click(screen.getByRole("button", { name: /^stop$/i }));
    expect(hoisted.stopMock).toHaveBeenCalledOnce();
    expect(screen.getByRole("button", { name: /play/i })).toBeInTheDocument();
  });

  it("switching mode while playing calls setMode on the live player", async () => {
    mockSdr(915_000_000);
    render(<AudioPlayer />);
    await userEvent.click(screen.getByRole("button", { name: /play/i }));
    hoisted.playMock.mockClear();

    await userEvent.click(screen.getByRole("button", { name: "AM" }));

    expect(hoisted.setModeMock).toHaveBeenCalledWith("am");
    expect(hoisted.playMock).not.toHaveBeenCalled(); // no reconnect, just a mode frame
  });

  it("switching mode before playing does not touch the player", async () => {
    mockSdr(915_000_000);
    render(<AudioPlayer />);
    await userEvent.click(screen.getByRole("button", { name: "NBFM" }));
    expect(hoisted.setModeMock).not.toHaveBeenCalled();
    expect(hoisted.playMock).not.toHaveBeenCalled();
  });
});
