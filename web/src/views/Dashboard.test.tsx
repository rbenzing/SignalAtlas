import { describe, it, expect, vi, afterEach } from "vitest";
import { render, screen, within } from "@testing-library/react";
import Dashboard from "./Dashboard";
import * as api from "../api";
import * as live from "../live/LiveProvider";

/**
 * A live store with nothing in it — the SHIPPED DEFAULT state. `SeedDemoData` is off by default
 * (an offline install shows honest empty state), so this is the very first screen a new operator
 * sees. "Zero emitters" and "emitter count unavailable" mean different things in an RF tool, so the
 * tiles must render 0, not a placeholder dash.
 */
function emptyLive(): ReturnType<typeof live.useLive> {
  return {
    status: "connected",
    signals: [],
    emitters: [],
    alerts: [],
    spectrumFrames: [],
    spectrumFps: 0,
  } as ReturnType<typeof live.useLive>;
}

afterEach(() => {
  vi.restoreAllMocks();
});

describe("Dashboard stat tiles", () => {
  it("renders a real zero (not a dash) when every count is legitimately zero", async () => {
    vi.spyOn(live, "useLive").mockReturnValue(emptyLive());
    vi.spyOn(api, "getSummary").mockResolvedValue({
      signalCount: 0,
      deviceCount: 0,
      alertCount: 0,
    });

    render(<Dashboard />);

    // Query each tile by its accessible name — precise, and it asserts the a11y contract too.
    for (const label of ["Signals", "Devices", "Emitters", "Alerts"]) {
      const tile = await screen.findByRole("group", { name: `${label}: 0` });
      expect(within(tile).getByText("0"), `${label} shows 0`).toBeTruthy();
      expect(within(tile).queryByText("—"), `${label} shows no dash`).toBeNull();
    }
  });

  it("still shows zero for emitters when the summary endpoint fails", async () => {
    // Regression guard: the tiles must not depend on /summary to render a known-zero live count.
    vi.spyOn(live, "useLive").mockReturnValue(emptyLive());
    vi.spyOn(api, "getSummary").mockRejectedValue(new Error("summary unavailable"));

    render(<Dashboard />);

    const tile = await screen.findByRole("group", { name: "Emitters: 0" });
    expect(within(tile).getByText("0")).toBeTruthy();
  });

  it("labels each stat tile so the value is never an unlabelled heading", async () => {
    // a11y: a screen reader navigating by heading must not hear "0, 0, 0, 0".
    vi.spyOn(live, "useLive").mockReturnValue(emptyLive());
    vi.spyOn(api, "getSummary").mockResolvedValue({
      signalCount: 0,
      deviceCount: 0,
      alertCount: 0,
    });

    render(<Dashboard />);

    expect(await screen.findByRole("group", { name: /Emitters: 0/i })).toBeTruthy();
  });
});
