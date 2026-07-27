import { describe, it, expect } from "vitest";
import {
  aircraftToGeoJSON,
  emittersToGeoJSON,
  collectFitCoordinates,
  heatmapCellsToGeoJSON,
} from "./rfmap";
import type { Device, Emitter, HeatmapCell } from "../api";

function device(over: Partial<Device>): Device {
  return {
    id: "40621D", deviceType: "Aircraft", primaryIdentifier: "40621D",
    identifiers: { icao: "40621D" }, vendor: null, protocol: "ADS-B",
    confidence: 1, evidence: [], latitude: null, longitude: null, altitudeFt: null,
    ...over,
  } as Device;
}

function emitter(over: Partial<Emitter>): Emitter {
  return {
    id: "e1", deviceId: null, protocol: "lora", freqCenterHz: 915e6, freqStabilityHz: 100,
    confidence: 0.9, signalCount: 3,
    estLatitude: 42.3601, estLongitude: -71.0589, estUncertaintyM: 50,
    identifiers: {}, evidence: [{ feature: "test", value: "x", weight: 1 }],
    ...over,
  } as Emitter;
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

describe("heatmapCellsToGeoJSON", () => {
  const cell = (over: Partial<HeatmapCell>): HeatmapCell => ({
    lat: 42.36,
    lon: -71.06,
    count: 5,
    ...over,
  });

  it("maps each density cell to a [lon,lat] point weighted by its count", () => {
    const fc = heatmapCellsToGeoJSON([cell({ lat: 42.36, lon: -71.06, count: 7 })]);
    expect(fc.features).toHaveLength(1);
    expect(fc.features[0].geometry.coordinates).toEqual([-71.06, 42.36]);
    expect(fc.features[0].properties.count).toBe(7);
    expect(fc.features[0].properties.weight).toBe(7);
  });

  it("floors the weight at 1 so a single-observation cell still renders", () => {
    const fc = heatmapCellsToGeoJSON([cell({ count: 0 })]);
    expect(fc.features[0].properties.weight).toBe(1);
  });

  it("skips cells whose coordinates are not finite", () => {
    const fc = heatmapCellsToGeoJSON([
      cell({ lat: Number.NaN, lon: -71.06 }),
      cell({ lat: 42.36, lon: Number.POSITIVE_INFINITY }),
      cell({ lat: 42.36, lon: -71.06 }),
    ]);
    expect(fc.features).toHaveLength(1);
  });
});

describe("collectFitCoordinates", () => {
  it("includes aircraft coordinates alongside emitter coordinates", () => {
    // Regression: the RF Map's initial fitBounds used to be computed from emitter
    // features only, so a positioned aircraft far from the emitters (e.g. Boston
    // emitters vs. an Amsterdam-area aircraft) was fitted out of view and never shown.
    const { fc: emitterFc } = emittersToGeoJSON(
      [emitter({ id: "boston-1", estLatitude: 42.3601, estLongitude: -71.0589 })],
      "dark",
    );
    const aircraftFc = aircraftToGeoJSON([
      device({ id: "ams-1", latitude: 52.3667, longitude: 4.9, altitudeFt: 38000 }),
    ]);

    const coords = collectFitCoordinates(emitterFc, aircraftFc);

    expect(coords).toContainEqual([-71.0589, 42.3601]);
    expect(coords).toContainEqual([4.9, 52.3667]);
    expect(coords).toHaveLength(2);
  });

  it("returns aircraft-only coordinates when there are no emitters", () => {
    const { fc: emitterFc } = emittersToGeoJSON([], "dark");
    const aircraftFc = aircraftToGeoJSON([
      device({ id: "ams-1", latitude: 52.3667, longitude: 4.9, altitudeFt: 38000 }),
    ]);

    const coords = collectFitCoordinates(emitterFc, aircraftFc);

    expect(coords).toEqual([[4.9, 52.3667]]);
  });

  it("returns an empty list when neither emitters nor aircraft are placeable", () => {
    const { fc: emitterFc } = emittersToGeoJSON([], "dark");
    const aircraftFc = aircraftToGeoJSON([device({ id: "b", latitude: null, longitude: null })]);

    expect(collectFitCoordinates(emitterFc, aircraftFc)).toEqual([]);
  });
});
