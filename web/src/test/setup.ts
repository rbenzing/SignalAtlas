import "@testing-library/jest-dom";

// jsdom does not implement URL.createObjectURL; maplibre-gl calls it at
// module-load time to spin up its worker blob. Polyfill so importing
// maplibre-gl-backed modules (e.g. src/lib/basemap.ts) doesn't crash in tests.
if (typeof window !== "undefined" && !window.URL.createObjectURL) {
  window.URL.createObjectURL = () => "blob:mock";
}
if (typeof window !== "undefined" && !window.URL.revokeObjectURL) {
  window.URL.revokeObjectURL = () => {};
}
