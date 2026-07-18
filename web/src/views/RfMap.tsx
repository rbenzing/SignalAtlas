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
import Divider from "@mui/material/Divider";
import Stack from "@mui/material/Stack";
import FormControlLabel from "@mui/material/FormControlLabel";
import Switch from "@mui/material/Switch";
import CloseIcon from "@mui/icons-material/Close";
import ChartCard from "../components/ChartCard";
import ProtocolLegend from "../components/ProtocolLegend";
import EvidenceList from "../components/EvidenceList";
import Loading from "../components/Loading";
import ErrorState from "../components/ErrorState";
import { useColorMode } from "../theme/ColorModeContext";
import { chartTokens, protocolColor, protocolLabels } from "../theme/palette";
import { getEmitters, getDevices, usePolling, type Emitter, type Device } from "../api";
import {
  emittersToGeoJSON,
  rfLayers,
  buildGraticule,
  graticuleSpacing,
  graticuleLayer,
  aircraftToGeoJSON,
  aircraftLayer,
  RF_SOURCE,
  RF_POINT_LAYER,
  RF_HEATMAP_LAYER,
  RF_GRATICULE_SOURCE,
  RF_GRATICULE_LAYER,
  AIRCRAFT_SOURCE,
  AIRCRAFT_POINT_LAYER,
} from "../lib/rfmap";
import { resolveBaseStyle } from "../lib/basemap";

const MAP_H = 480;

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

    const aSrc = map.getSource(AIRCRAFT_SOURCE) as GeoJSONSource | undefined;
    aSrc?.setData(aircraftToGeoJSON(devices.data ?? []));
    map.setPaintProperty(AIRCRAFT_POINT_LAYER, "circle-stroke-color", chartTokens[mode].surface);

    if (!fittedRef.current && fc.features.length > 0) {
      const bounds = new LngLatBounds();
      for (const f of fc.features) {
        bounds.extend(f.geometry.coordinates as [number, number]);
      }
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

  const placed = (data?.length ?? 0) - unplaceable;

  return (
    <ChartCard
      title="RF Map"
      legend={
        <Box sx={{ display: "flex", alignItems: "center", gap: 2, flexWrap: "wrap" }}>
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
          <ProtocolLegend />
          <Box sx={{ display: "flex", alignItems: "center", gap: 0.5 }}>
            <Box
              sx={{
                width: 10,
                height: 10,
                borderRadius: "50%",
                bgcolor: "#f5a623",
              }}
            />
            <Typography variant="caption">Aircraft</Typography>
          </Box>
        </Box>
      }
    >
      {loading && !data && <Loading />}
      {error && <ErrorState message={error} />}
      {data && placed === 0 && (
        <Typography variant="body2" color="text.secondary" sx={{ mb: 1 }}>
          No placeable emitters to display.
        </Typography>
      )}
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
              left: 8,
              bottom: 8,
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
      </Box>

      <Drawer anchor="right" open={selected !== null} onClose={() => setSelected(null)}>
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
                      bgcolor: "#f5a623",
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
