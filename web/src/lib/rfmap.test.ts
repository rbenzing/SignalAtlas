import { describe, it, expect } from "vitest";
import { aircraftToGeoJSON } from "./rfmap";
import type { Device } from "../api";

function device(over: Partial<Device>): Device {
  return {
    id: "40621D", deviceType: "Aircraft", primaryIdentifier: "40621D",
    identifiers: { icao: "40621D" }, vendor: null, protocol: "ADS-B",
    confidence: 1, evidence: [], latitude: null, longitude: null, altitudeFt: null,
    ...over,
  } as Device;
}

describe("aircraftToGeoJSON", () => {
  it("includes only devices with a position fix", () => {
    const fc = aircraftToGeoJSON([
      device({ id: "A", latitude: 52.2572, longitude: 3.91937, altitudeFt: 38000 }),
      device({ id: "B", latitude: null, longitude: null }), // no fix → excluded
    ]);
    expect(fc.features).toHaveLength(1);
    expect(fc.features[0].properties.id).toBe("A");
    expect(fc.features[0].geometry.coordinates).toEqual([3.91937, 52.2572]);
  });
});
