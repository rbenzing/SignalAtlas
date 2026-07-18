import { describe, it, expect, afterEach, vi } from "vitest";
import {
  esriBasemaps,
  rasterSourcesAndLayers,
  basemapLabels,
  defaultBasemapId,
  attributionFor,
  configuredBasemapStyle,
  resolveBaseStyle,
} from "./basemap";

describe("ESRI basemap registry", () => {
  it("defines three ESRI raster basemaps with World_* tile templates", () => {
    const b = esriBasemaps();
    expect(b.map((x) => x.id).sort()).toEqual(["esri-imagery", "esri-street", "esri-topo"]);
    for (const x of b) {
      expect(x.tiles[0]).toMatch(/server\.arcgisonline\.com\/ArcGIS\/rest\/services\/World_/);
      expect(x.tiles[0]).toContain("/tile/{z}/{y}/{x}");
      expect(x.attribution.length).toBeGreaterThan(0);
      expect(x.maxzoom).toBeGreaterThan(0);
    }
    expect(new Set(b.map((x) => x.sourceId)).size).toBe(3); // unique source ids
    expect(new Set(b.map((x) => x.layerId)).size).toBe(3);  // unique layer ids
  });

  it("builds hidden raster sources+layers", () => {
    const pairs = rasterSourcesAndLayers();
    expect(pairs).toHaveLength(3);
    for (const p of pairs) {
      expect((p.source as { type: string }).type).toBe("raster");
      expect((p.source as { tileSize: number }).tileSize).toBe(256);
      expect((p.layer as { type: string }).type).toBe("raster");
      expect((p.layer as { layout?: { visibility?: string } }).layout?.visibility).toBe("none");
    }
  });

  it("lists Offline first then the three ESRI labels", () => {
    const labels = basemapLabels();
    expect(labels[0]).toEqual({ id: "offline", label: "Offline" });
    expect(labels.map((l) => l.id)).toEqual(["offline", "esri-imagery", "esri-street", "esri-topo"]);
  });

  it("defaults to offline and reports attribution per basemap", () => {
    expect(defaultBasemapId()).toBe("offline"); // VITE_BASEMAP_STYLE unset in test env
    expect(attributionFor("offline")).toBeNull();
    expect(attributionFor("esri-imagery")).not.toBeNull();
  });
});

describe("VITE_BASEMAP_STYLE handling", () => {
  afterEach(() => vi.unstubAllEnvs());

  it("treats an ESRI id as the switcher default, not a style URL", () => {
    vi.stubEnv("VITE_BASEMAP_STYLE", "esri-imagery");
    expect(configuredBasemapStyle()).toBeUndefined(); // NOT returned as a style URL
    expect(defaultBasemapId()).toBe("esri-imagery"); // becomes the switcher default
    expect(typeof resolveBaseStyle("light")).not.toBe("string"); // falls back to the blank StyleSpecification
  });

  it("still passes a real custom style URL through", () => {
    vi.stubEnv("VITE_BASEMAP_STYLE", "/basemap.json");
    expect(configuredBasemapStyle()).toBe("/basemap.json");
  });
});
