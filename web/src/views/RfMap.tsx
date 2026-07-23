import { useEffect, useRef, useState } from "react";
import {
  MapLibreMap,
  LngLatBounds,
  NavigationControl,
  ScaleControl,
  type GeoJSONSource,
  type MapLayerMouseEvent,
} from "maplibre-gl";
import "maplibre-gl/dist/maplibre-gl.css";
import Box from "@mui/material/Box";
import Drawer from "@mui/material/Drawer";
import Typography from "@mui/material/Typography";
import IconButton from "@mui/material/IconButton";
import Toolbar from "@mui/material/Toolbar";
import Divider from "@mui/material/Divider";
import Stack from "@mui/material/Stack";
import FormControlLabel from "@mui/material/FormControlLabel";
import Switch from "@mui/material/Switch";
import ToggleButton from "@mui/material/ToggleButton";
import ToggleButtonGroup from "@mui/material/ToggleButtonGroup";
import CloseIcon from "@mui/icons-material/Close";
import ChartCard from "../components/ChartCard";
import ProtocolLegend from "../components/ProtocolLegend";
import EvidenceList from "../components/EvidenceList";
import Loading from "../components/Loading";
import ErrorState from "../components/ErrorState";
import { useColorMode } from "../theme/ColorModeContext";
import { chartTokens, protocolColor, protocolLabels } from "../theme/palette";
import {
  getEmitters,
  getDevices,
  getDeviceGeo,
  getDeviceImage,
  usePolling,
  type Emitter,
  type Device,
} from "../api";
import {
  emittersToGeoJSON,
  rfLayers,
  buildGraticule,
  graticuleSpacing,
  graticuleLayer,
  aircraftToGeoJSON,
  aircraftLayer,
  collectFitCoordinates,
  RF_SOURCE,
  RF_POINT_LAYER,
  RF_HEATMAP_LAYER,
  RF_GRATICULE_SOURCE,
  RF_GRATICULE_LAYER,
  AIRCRAFT_SOURCE,
  AIRCRAFT_POINT_LAYER,
  AIRCRAFT_COLOR,
} from "../lib/rfmap";
import {
  resolveBaseStyle,
  rasterSourcesAndLayers,
  basemapLabels,
  defaultBasemapId,
  attributionFor,
  esriBasemaps,
  type BasemapId,
} from "../lib/basemap";

const MAP_H = 480;

const NOAA_APT_PROTOCOL = "NOAA-APT";

/** Deterministic MapLibre source/layer id for a device's weather-overlay image (SPEC §8.4 Phase 2). */
function aptOverlayId(deviceId: string): string {
  return `apt-img-${deviceId}`;
}

/** Add the image source + raster layer for one device's weather overlay, above the basemap/graticule
 * but below the emitter/aircraft point layers. Guarded so a duplicate add never throws. */
function addWeatherOverlay(
  map: MapLibreMap,
  deviceId: string,
  imageUrl: string,
  corners: number[][],
) {
  const id = aptOverlayId(deviceId);
  if (map.getSource(id) || map.getLayer(id)) return;
  map.addSource(id, {
    type: "image",
    url: imageUrl,
    coordinates: corners as [[number, number], [number, number], [number, number], [number, number]],
  });
  map.addLayer(
    {
      id,
      type: "raster",
      source: id,
      paint: { "raster-opacity": 0.75 },
    },
    map.getLayer(RF_POINT_LAYER) ? RF_POINT_LAYER : undefined,
  );
}

/** Remove one device's weather-overlay layer + source (no-op if absent). */
function removeWeatherOverlay(map: MapLibreMap, deviceId: string) {
  const id = aptOverlayId(deviceId);
  if (map.getLayer(id)) map.removeLayer(id);
  if (map.getSource(id)) map.removeSource(id);
}

function DetailRow({ label, value }: { label: string; value: React.ReactNode }) {
  return (
    <Box sx={{ display: "flex", justifyContent: "space-between", gap: 2 }}>
      <Typography variant="caption" color="text.secondary">
        {label}
      </Typography>
      <Typography variant="body2" sx={{ fontVariantNumeric: "tabular-nums", textAlign: "right" }}>
        {value}
      </Typography>
    </Box>
  );
}

export default function RfMap() {
  const { mode } = useColorMode();
  const { data, loading, error } = usePolling(getEmitters, 5000);
  const devices = usePolling(getDevices, 5000);

  const containerRef = useRef<HTMLDivElement | null>(null);
  const mapRef = useRef<MapLibreMap | null>(null);
  const dataRef = useRef<Emitter[]>([]);
  const aircraftRef = useRef<Device[]>([]);
  const fittedRef = useRef(false);
  const [mapReady, setMapReady] = useState(false);
  const [heatmap, setHeatmap] = useState(false);
  const [weather, setWeather] = useState(false);
  // deviceId -> object URL for the currently-added weather overlays (so toggles/unmount can revoke).
  const weatherOverlaysRef = useRef<Map<string, string>>(new Map());
  const [basemap, setBasemap] = useState<BasemapId>(defaultBasemapId());
  const [unplaceable, setUnplaceable] = useState(0);
  const [selected, setSelected] = useState<Emitter | null>(null);
  const [selectedAircraft, setSelectedAircraft] = useState<Device | null>(null);

  dataRef.current = data ?? [];
  aircraftRef.current = devices.data ?? [];

  // Init the map once; add the source, layers, and interactions on load.
  useEffect(() => {
    if (!containerRef.current) return;
    const map = new MapLibreMap({
      container: containerRef.current,
      // Offline blank plane by default; a real basemap iff VITE_BASEMAP_STYLE set.
      style: resolveBaseStyle(mode),
      center: [0, 0],
      zoom: 1,
      attributionControl: false,
    });
    mapRef.current = map;

    // Real map controls (default MapLibre CSS themes acceptably light/dark).
    map.addControl(new NavigationControl({ visualizePitch: true }), "top-right");
    map.addControl(new ScaleControl({ unit: "metric" }), "bottom-right");

    const onClick = (e: MapLayerMouseEvent) => {
      const id = e.features?.[0]?.properties?.id as string | undefined;
      if (!id) return;
      setSelected(dataRef.current.find((x) => x.id === id) ?? null);
    };
    const onEnter = () => {
      map.getCanvas().style.cursor = "pointer";
    };
    const onLeave = () => {
      map.getCanvas().style.cursor = "";
    };
    // Regenerate the graticule for the current viewport + adaptive spacing.
    const updateGraticule = () => {
      const src = map.getSource(RF_GRATICULE_SOURCE) as GeoJSONSource | undefined;
      if (!src) return;
      const b = map.getBounds();
      const fc = buildGraticule(
        { west: b.getWest(), south: b.getSouth(), east: b.getEast(), north: b.getNorth() },
        graticuleSpacing(map.getZoom()),
      );
      src.setData(fc);
    };

    map.on("load", () => {
      // ESRI raster basemaps (hidden until selected — offline-first). Added first so the graticule,
      // emitters, and aircraft layers stack above whatever basemap is active.
      for (const pair of rasterSourcesAndLayers()) {
        map.addSource(pair.sourceId, pair.source);
        map.addLayer(pair.layer);
      }
      // Graticule (bottom) — added first so emitter layers render above it.
      map.addSource(RF_GRATICULE_SOURCE, {
        type: "geojson",
        data: { type: "FeatureCollection", features: [] },
      });
      map.addLayer(graticuleLayer(mode));
      map.addSource(RF_SOURCE, {
        type: "geojson",
        data: { type: "FeatureCollection", features: [] },
      });
      for (const layer of rfLayers(mode)) map.addLayer(layer);
      map.on("click", RF_POINT_LAYER, onClick);
      map.on("mouseenter", RF_POINT_LAYER, onEnter);
      map.on("mouseleave", RF_POINT_LAYER, onLeave);

      map.addSource(AIRCRAFT_SOURCE, { type: "geojson", data: { type: "FeatureCollection", features: [] } });
      map.addLayer(aircraftLayer(mode));
      map.on("click", AIRCRAFT_POINT_LAYER, (e) => {
        const id = e.features?.[0]?.properties?.id as string | undefined;
        if (id) setSelectedAircraft(aircraftRef.current.find((a) => a.id === id) ?? null);
      });
      map.on("mouseenter", AIRCRAFT_POINT_LAYER, onEnter);
      map.on("mouseleave", AIRCRAFT_POINT_LAYER, onLeave);

      map.on("moveend", updateGraticule);
      updateGraticule();
      setMapReady(true);
    });

    return () => {
      map.remove();
      mapRef.current = null;
      setMapReady(false);
      // Revoke any outstanding weather-overlay object URLs — the map (and its sources) are gone,
      // so there's nothing left to remove them from, but the blob URLs themselves would leak.
      for (const url of weatherOverlaysRef.current.values()) URL.revokeObjectURL(url);
      weatherOverlaysRef.current.clear();
    };
    // Init once — mode changes are handled by the paint/data effect below.
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  // Push data + re-theme when emitters or color mode change.
  useEffect(() => {
    const map = mapRef.current;
    if (!map || !mapReady) return;
    const { fc, unplaceable: skipped } = emittersToGeoJSON(data ?? [], mode);
    setUnplaceable(skipped);
    const src = map.getSource(RF_SOURCE) as GeoJSONSource | undefined;
    src?.setData(fc);
    // page-plane only exists on the blank offline style; skip under a real basemap.
    if (map.getLayer("page-plane")) {
      map.setPaintProperty("page-plane", "background-color", chartTokens[mode].surfaceAlt);
    }
    map.setPaintProperty(RF_GRATICULE_LAYER, "line-color", chartTokens[mode].grid);
    map.setPaintProperty(RF_POINT_LAYER, "circle-stroke-color", chartTokens[mode].surface);

    const aircraftFc = aircraftToGeoJSON(devices.data ?? []);
    const aSrc = map.getSource(AIRCRAFT_SOURCE) as GeoJSONSource | undefined;
    aSrc?.setData(aircraftFc);
    map.setPaintProperty(AIRCRAFT_POINT_LAYER, "circle-stroke-color", chartTokens[mode].surface);

    // Fit the initial view to emitters AND aircraft together — fitting to emitters alone can
    // leave positioned aircraft (which may be nowhere near the emitters) off-screen forever.
    const fitCoords = collectFitCoordinates(fc, aircraftFc);
    if (!fittedRef.current && fitCoords.length > 0) {
      const bounds = new LngLatBounds();
      for (const c of fitCoords) bounds.extend(c);
      map.fitBounds(bounds, { padding: 64, maxZoom: 16, duration: 0 });
      fittedRef.current = true;
    }
  }, [data, devices.data, mode, mapReady]);

  // Heatmap layer visibility toggle.
  useEffect(() => {
    const map = mapRef.current;
    if (!map || !mapReady) return;
    map.setLayoutProperty(RF_HEATMAP_LAYER, "visibility", heatmap ? "visible" : "none");
  }, [heatmap, mapReady]);

  // Basemap switcher: flip ESRI raster-layer visibility. Offline → all hidden (blank plane + graticule).
  useEffect(() => {
    const map = mapRef.current;
    if (!map || !mapReady) return;
    for (const b of esriBasemaps()) {
      if (map.getLayer(b.layerId)) {
        map.setLayoutProperty(b.layerId, "visibility", basemap === b.id ? "visible" : "none");
      }
    }
  }, [basemap, mapReady]);

  // Weather overlay: when ON, fetch geo quad + PNG for each NOAA-APT device and add an image
  // source/raster layer; when OFF (or a device drops out of the device list), remove it + revoke
  // its object URL. Devices already overlaid are left alone (no re-fetch on every poll tick).
  useEffect(() => {
    const map = mapRef.current;
    if (!map || !mapReady) return;
    let cancelled = false;
    const overlays = weatherOverlaysRef.current;

    const aptDevices = weather
      ? (devices.data ?? []).filter((d) => d.protocol === NOAA_APT_PROTOCOL)
      : [];
    const wantedIds = new Set(aptDevices.map((d) => d.id));

    // Drop overlays that are no longer wanted (toggle off, or the device disappeared).
    for (const [id, url] of overlays) {
      if (!wantedIds.has(id)) {
        removeWeatherOverlay(map, id);
        URL.revokeObjectURL(url);
        overlays.delete(id);
      }
    }

    if (weather) {
      void (async () => {
        for (const device of aptDevices) {
          if (overlays.has(device.id)) continue;
          const [quad, blob] = await Promise.all([
            getDeviceGeo(device.id),
            getDeviceImage(device.id),
          ]);
          if (cancelled) return;
          // No TLE / no fix / no image yet — offline-degrading, simply no overlay for this device.
          if (!quad || !blob) continue;
          if (overlays.has(device.id) || !mapRef.current) continue;
          const url = URL.createObjectURL(blob);
          addWeatherOverlay(mapRef.current, device.id, url, quad.corners);
          overlays.set(device.id, url);
        }
      })();
    }

    return () => {
      cancelled = true;
    };
  }, [weather, devices.data, mapReady]);

  const placed = (data?.length ?? 0) - unplaceable;
  const positionedAircraft = (devices.data ?? []).filter(
    (d) =>
      d.latitude !== null &&
      d.longitude !== null &&
      Number.isFinite(d.latitude) &&
      Number.isFinite(d.longitude),
  ).length;
  // Both feeds have returned at least once, neither errored, and nothing on the map has a fix.
  const feedsReady = data !== undefined && devices.data !== undefined;
  const feedError = error ?? devices.error;
  const noContacts = feedsReady && !feedError && placed === 0 && positionedAircraft === 0;

  return (
    <ChartCard
      title="RF Map"
      legend={
        <Box sx={{ display: "flex", alignItems: "center", gap: 2, flexWrap: "wrap" }}>
          <ToggleButtonGroup
            size="small"
            exclusive
            value={basemap}
            onChange={(_, v) => { if (v) setBasemap(v as BasemapId); }}
            aria-label="Basemap"
          >
            {basemapLabels().map((b) => (
              <ToggleButton key={b.id} value={b.id} sx={{ textTransform: "none", px: 1 }}>
                {b.label}
              </ToggleButton>
            ))}
          </ToggleButtonGroup>
          <FormControlLabel
            control={
              <Switch
                size="small"
                checked={heatmap}
                onChange={(e) => setHeatmap(e.target.checked)}
              />
            }
            label={<Typography variant="caption">Heatmap</Typography>}
          />
          <FormControlLabel
            control={
              <Switch
                size="small"
                checked={weather}
                onChange={(e) => setWeather(e.target.checked)}
              />
            }
            label={<Typography variant="caption">Weather overlay</Typography>}
          />
          <ProtocolLegend />
          <Box sx={{ display: "flex", alignItems: "center", gap: 0.5 }}>
            <Box
              sx={{
                width: 10,
                height: 10,
                borderRadius: "50%",
                bgcolor: AIRCRAFT_COLOR,
              }}
            />
            <Typography variant="caption">Aircraft</Typography>
          </Box>
        </Box>
      }
    >
      {loading && !data && <Loading />}
      {feedError && <ErrorState message={feedError} />}
      <Box sx={{ position: "relative" }}>
        <Box
          ref={containerRef}
          sx={{
            height: MAP_H,
            width: "100%",
            borderRadius: 1,
            overflow: "hidden",
            border: 1,
            borderColor: "divider",
          }}
        />
        {unplaceable > 0 && (
          <Box
            sx={{
              position: "absolute",
              top: 8,
              left: 8,
              px: 1,
              py: 0.5,
              borderRadius: 1,
              bgcolor: "background.paper",
              border: 1,
              borderColor: "divider",
            }}
          >
            <Typography variant="caption" color="text.secondary">
              {unplaceable} unplaceable emitter{unplaceable === 1 ? "" : "s"}
            </Typography>
          </Box>
        )}
        {/* Nothing has a location fix — tell the operator WHY the map is empty and what antenna/band
            is needed, rather than showing a blank plane (a common "wrong antenna / not tuned" case). */}
        {noContacts && (
          <Box
            sx={{
              position: "absolute",
              inset: 0,
              display: "flex",
              alignItems: "center",
              justifyContent: "center",
              p: 2,
              pointerEvents: "none",
            }}
          >
            <Box
              sx={{
                maxWidth: 440,
                bgcolor: "background.paper",
                border: 1,
                borderColor: "divider",
                borderRadius: 2,
                p: 2.5,
                opacity: 0.97,
                boxShadow: 3,
              }}
            >
              <Typography variant="subtitle2" gutterBottom>
                No positioned contacts yet
              </Typography>
              <Typography variant="body2" color="text.secondary">
                Nothing on the map has a location fix. Connect an antenna and tune the matching band
                from the navbar to see contacts:
              </Typography>
              <Box component="ul" sx={{ pl: 2.5, my: 1, "& li": { mb: 0.5 } }}>
                <Typography component="li" variant="caption" color="text.secondary">
                  <b>Aircraft</b> (amber) — an <b>ADS-B 1090&nbsp;MHz</b> antenna; a dot appears once
                  an even/odd position pair (CPR) is decoded.
                </Typography>
                <Typography component="li" variant="caption" color="text.secondary">
                  <b>Emitters</b> — appear once RF fixes are correlated.
                </Typography>
                <Typography component="li" variant="caption" color="text.secondary">
                  <b>Weather imagery</b> — a <b>NOAA APT 137&nbsp;MHz</b> antenna (shown in the device
                  drawer).
                </Typography>
              </Box>
              <Typography variant="caption" color="text.secondary">
                Receiving on the wrong band or without an antenna will leave this map empty.
              </Typography>
            </Box>
          </Box>
        )}
        {attributionFor(basemap) && (
          <Box
            sx={{
              position: "absolute",
              left: 8,
              bottom: 8,
              px: 1,
              py: 0.25,
              borderRadius: 1,
              bgcolor: "background.paper",
              border: 1,
              borderColor: "divider",
              maxWidth: "70%",
            }}
          >
            <Typography variant="caption" color="text.secondary" sx={{ fontSize: 10 }}>
              {attributionFor(basemap)}
            </Typography>
          </Box>
        )}
      </Box>

      <Drawer anchor="right" open={selected !== null} onClose={() => setSelected(null)}>
        <Toolbar />
        <Box sx={{ width: 340, p: 2 }}>
          {selected && (
            <>
              <Box sx={{ display: "flex", alignItems: "center", justifyContent: "space-between" }}>
                <Box sx={{ display: "flex", alignItems: "center", gap: 1 }}>
                  <Box
                    sx={{
                      width: 12,
                      height: 12,
                      borderRadius: "50%",
                      bgcolor: protocolColor(selected.protocol, mode),
                    }}
                  />
                  <Typography variant="h6">
                    {protocolLabels[selected.protocol.trim().toLowerCase()] ?? selected.protocol}
                  </Typography>
                </Box>
                <IconButton size="small" aria-label="Close" onClick={() => setSelected(null)}>
                  <CloseIcon fontSize="small" />
                </IconButton>
              </Box>
              <Typography variant="caption" color="text.secondary">
                {selected.id}
              </Typography>

              <Divider sx={{ my: 1.5 }} />
              <Stack spacing={0.75}>
                <DetailRow label="Freq" value={`${(selected.freqCenterHz / 1e6).toFixed(3)} MHz`} />
                <DetailRow
                  label="Confidence"
                  value={`${(selected.confidence * 100).toFixed(0)}%`}
                />
                <DetailRow label="Signals" value={selected.signalCount.toLocaleString()} />
                <DetailRow
                  label="Uncertainty"
                  value={
                    selected.estUncertaintyM === null
                      ? "—"
                      : `± ${selected.estUncertaintyM.toLocaleString()} m`
                  }
                />
                <DetailRow
                  label="Location"
                  value={
                    selected.estLatitude === null || selected.estLongitude === null
                      ? "—"
                      : `${selected.estLatitude.toFixed(4)}, ${selected.estLongitude.toFixed(4)}`
                  }
                />
              </Stack>

              {Object.keys(selected.identifiers ?? {}).length > 0 && (
                <>
                  <Divider sx={{ my: 1.5 }} />
                  <Typography variant="subtitle2" sx={{ mb: 0.5 }}>
                    Identifiers
                  </Typography>
                  <Stack spacing={0.5}>
                    {Object.entries(selected.identifiers).map(([k, v]) => (
                      <DetailRow key={k} label={k} value={v} />
                    ))}
                  </Stack>
                </>
              )}

              <Divider sx={{ my: 1.5 }} />
              <Typography variant="subtitle2" sx={{ mb: 0.5 }}>
                Evidence
              </Typography>
              <EvidenceList items={selected.evidence} />
            </>
          )}
        </Box>
      </Drawer>

      <Drawer
        anchor="right"
        open={selectedAircraft !== null}
        onClose={() => setSelectedAircraft(null)}
      >
        <Toolbar />
        <Box sx={{ width: 340, p: 2 }}>
          {selectedAircraft && (
            <>
              <Box sx={{ display: "flex", alignItems: "center", justifyContent: "space-between" }}>
                <Box sx={{ display: "flex", alignItems: "center", gap: 1 }}>
                  <Box
                    sx={{
                      width: 12,
                      height: 12,
                      borderRadius: "50%",
                      bgcolor: AIRCRAFT_COLOR,
                    }}
                  />
                  <Typography variant="h6">
                    {selectedAircraft.identifiers?.callsign?.trim() || selectedAircraft.id}
                  </Typography>
                </Box>
                <IconButton
                  size="small"
                  aria-label="Close"
                  onClick={() => setSelectedAircraft(null)}
                >
                  <CloseIcon fontSize="small" />
                </IconButton>
              </Box>
              <Typography variant="caption" color="text.secondary">
                {selectedAircraft.id}
              </Typography>

              <Divider sx={{ my: 1.5 }} />
              <Stack spacing={0.75}>
                <DetailRow
                  label="ICAO"
                  value={selectedAircraft.identifiers?.icao ?? selectedAircraft.id}
                />
                <DetailRow
                  label="Altitude"
                  value={
                    selectedAircraft.altitudeFt === null
                      ? "—"
                      : `${selectedAircraft.altitudeFt.toLocaleString()} ft`
                  }
                />
                <DetailRow
                  label="Position"
                  value={
                    selectedAircraft.latitude === null || selectedAircraft.longitude === null
                      ? "—"
                      : `${selectedAircraft.latitude.toFixed(4)}, ${selectedAircraft.longitude.toFixed(4)}`
                  }
                />
              </Stack>

              <Divider sx={{ my: 1.5 }} />
              <Typography variant="subtitle2" sx={{ mb: 0.5 }}>
                Evidence
              </Typography>
              <EvidenceList items={selectedAircraft.evidence} />
            </>
          )}
        </Box>
      </Drawer>
    </ChartCard>
  );
}
