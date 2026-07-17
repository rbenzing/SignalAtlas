import { describe, it, expect, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import HackRfConnect from "./HackRfConnect";
import * as sdr from "../sdr/SdrProvider";

describe("HackRfConnect", () => {
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
});
