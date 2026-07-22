import { useEffect, useState } from "react";
import Box from "@mui/material/Box";
import Drawer from "@mui/material/Drawer";
import Divider from "@mui/material/Divider";
import Stack from "@mui/material/Stack";
import Typography from "@mui/material/Typography";
import IconButton from "@mui/material/IconButton";
import Toolbar from "@mui/material/Toolbar";
import CloseIcon from "@mui/icons-material/Close";
import ChartCard from "../components/ChartCard";
import DataTable, { type Column } from "../components/DataTable";
import EvidenceList from "../components/EvidenceList";
import ProtocolChip from "../components/ProtocolChip";
import ProtocolLegend from "../components/ProtocolLegend";
import Loading from "../components/Loading";
import ErrorState from "../components/ErrorState";
import { protocolLabels } from "../theme/palette";
import { getDeviceImage, getDevices, usePolling, type Device } from "../api";

const columns: Column<Device>[] = [
  {
    key: "deviceType",
    header: "Type",
    render: (d) => d.deviceType,
    sortValue: (d) => d.deviceType,
  },
  {
    key: "protocol",
    header: "Protocol",
    render: (d) => <ProtocolChip protocol={d.protocol} />,
    sortValue: (d) => d.protocol,
  },
  { key: "vendor", header: "Vendor", render: (d) => d.vendor ?? "—", sortValue: (d) => d.vendor ?? "" },
  {
    key: "primaryIdentifier",
    header: "Primary ID",
    render: (d) => d.primaryIdentifier ?? "—",
    sortValue: (d) => d.primaryIdentifier ?? "",
  },
  {
    key: "confidence",
    header: "Confidence",
    numeric: true,
    render: (d) => `${(d.confidence * 100).toFixed(0)}%`,
    sortValue: (d) => d.confidence,
  },
];

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

function SatelliteImage({ deviceId }: { deviceId: string }) {
  const [url, setUrl] = useState<string | null>(null);
  const [pending, setPending] = useState(true);
  useEffect(() => {
    let active = true;
    let objectUrl: string | null = null;
    getDeviceImage(deviceId).then((blob) => {
      if (!active) return;
      if (blob) {
        objectUrl = URL.createObjectURL(blob);
        setUrl(objectUrl);
      }
      setPending(false);
    });
    return () => {
      active = false;
      if (objectUrl) URL.revokeObjectURL(objectUrl);
    };
  }, [deviceId]);
  return (
    <>
      <Divider sx={{ my: 1.5 }} />
      <Typography variant="subtitle2" sx={{ mb: 0.5 }}>
        Decoded image
      </Typography>
      {url ? (
        <Box
          component="img"
          src={url}
          alt="Decoded APT image"
          sx={{
            width: "100%",
            imageRendering: "pixelated",
            borderRadius: 1,
            border: 1,
            borderColor: "divider",
          }}
        />
      ) : (
        <Typography variant="caption" color="text.secondary">
          {pending ? "Loading…" : "Image pending"}
        </Typography>
      )}
    </>
  );
}

export default function Devices() {
  const { data, loading, error } = usePolling(getDevices, 15000);
  const [selected, setSelected] = useState<Device | null>(null);

  const clickColumns: Column<Device>[] = columns.map((c) =>
    c.key === "deviceType"
      ? {
          ...c,
          render: (d) => (
            <Box
              component="button"
              onClick={() => setSelected(d)}
              sx={{
                border: 0,
                background: "none",
                p: 0,
                color: "primary.main",
                cursor: "pointer",
                font: "inherit",
                textAlign: "left",
              }}
            >
              {d.deviceType}
            </Box>
          ),
        }
      : c,
  );

  return (
    <ChartCard title="Devices" legend={<ProtocolLegend />}>
      {loading && !data && <Loading />}
      {error && <ErrorState message={error} />}
      {data && data.length === 0 && (
        <Typography variant="body2" color="text.secondary">
          No devices to display.
        </Typography>
      )}
      {data && data.length > 0 && (
        <DataTable columns={clickColumns} rows={data} rowKey={(d) => d.id} />
      )}

      <Drawer anchor="right" open={selected !== null} onClose={() => setSelected(null)}>
        <Toolbar />
        <Box sx={{ width: 340, p: 2 }}>
          {selected && (
            <>
              <Box sx={{ display: "flex", alignItems: "center", justifyContent: "space-between" }}>
                <Box sx={{ display: "flex", alignItems: "center", gap: 1 }}>
                  <ProtocolChip protocol={selected.protocol} />
                  <Typography variant="h6">{selected.deviceType}</Typography>
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
                <DetailRow
                  label="Protocol"
                  value={protocolLabels[selected.protocol.trim().toLowerCase()] ?? selected.protocol}
                />
                <DetailRow label="Vendor" value={selected.vendor ?? "—"} />
                <DetailRow label="Primary ID" value={selected.primaryIdentifier ?? "—"} />
                <DetailRow label="Confidence" value={`${(selected.confidence * 100).toFixed(0)}%`} />
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

              {selected.protocol === "NOAA-APT" && <SatelliteImage deviceId={selected.id} />}

              <Divider sx={{ my: 1.5 }} />
              <Typography variant="subtitle2" sx={{ mb: 0.5 }}>
                Evidence
              </Typography>
              <EvidenceList items={selected.evidence} />
            </>
          )}
        </Box>
      </Drawer>
    </ChartCard>
  );
}
