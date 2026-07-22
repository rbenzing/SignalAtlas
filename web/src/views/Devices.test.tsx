import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import Devices from "./Devices";
import * as api from "../api";
import type { Device } from "../api";

const NOAA_DEVICE: Device = {
  id: "dev-noaa-19",
  deviceType: "Satellite",
  primaryIdentifier: "NOAA-19",
  identifiers: { satellite: "NOAA-19" },
  vendor: null,
  protocol: "NOAA-APT",
  confidence: 0.9,
  evidence: [{ feature: "apt_sync", value: "locked", weight: 0.9 }],
  latitude: null,
  longitude: null,
  altitudeFt: null,
};

beforeEach(() => {
  URL.createObjectURL = vi.fn(() => "blob:mock-url");
  URL.revokeObjectURL = vi.fn();
});

afterEach(() => {
  vi.restoreAllMocks();
});

describe("Devices drawer image panel", () => {
  it("shows the decoded APT image for a NOAA-APT device", async () => {
    vi.spyOn(api, "getDevices").mockResolvedValue([NOAA_DEVICE]);
    const blob = new Blob([new Uint8Array([1, 2, 3])], { type: "image/png" });
    vi.spyOn(api, "getDeviceImage").mockResolvedValue(blob);

    render(<Devices />);

    const rowButton = await screen.findByRole("button", { name: /satellite/i });
    await userEvent.click(rowButton);

    const img = await screen.findByAltText("Decoded APT image");
    expect(img).toBeInTheDocument();
    expect(api.getDeviceImage).toHaveBeenCalledWith("dev-noaa-19");
  });

  it("does not render an image panel for a non-NOAA-APT device", async () => {
    vi.spyOn(api, "getDevices").mockResolvedValue([{ ...NOAA_DEVICE, protocol: "LoRa" }]);
    const getDeviceImage = vi.spyOn(api, "getDeviceImage");

    render(<Devices />);

    const rowButton = await screen.findByRole("button", { name: /satellite/i });
    await userEvent.click(rowButton);

    expect(screen.queryByAltText("Decoded APT image")).toBeNull();
    expect(getDeviceImage).not.toHaveBeenCalled();
  });
});
