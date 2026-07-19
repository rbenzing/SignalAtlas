import { useState } from "react";
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
import ConfidenceGauge from "../components/ConfidenceGauge";
import Loading from "../components/Loading";
import ErrorState from "../components/ErrorState";
import { useColorMode } from "../theme/ColorModeContext";
import { protocolColor, protocolLabels } from "../theme/palette";
import { getEmitters, usePolling, type Emitter } from "../api";

// estLatitude/estLongitude/estUncertaintyM are nullable — render "—" when absent.
function locationLabel(e: Emitter): string {
  if (e.estLatitude === null || e.estLongitude === null) return "—";
  return `${e.estLatitude.toFixed(4)}, ${e.estLongitude.toFixed(4)}`;
}

const columns: Column<Emitter>[] = [
  { key: "id", header: "Emitter", render: (e) => e.id, sortValue: (e) => e.id },
  {
    key: "protocol",
    header: "Protocol",
    render: (e) => <ProtocolChip protocol={e.protocol} />,
    sortValue: (e) => e.protocol,
  },
  {
    key: "freq",
    header: "Freq (MHz)",
    numeric: true,
    render: (e) => (e.freqCenterHz / 1e6).toFixed(3),
    sortValue: (e) => e.freqCenterHz,
  },
  {
    key: "confidence",
    header: "Confidence",
    numeric: true,
    render: (e) => `${(e.confidence * 100).toFixed(0)}%`,
    sortValue: (e) => e.confidence,
  },
  {
    key: "signalCount",
    header: "Signals",
    numeric: true,
    render: (e) => e.signalCount.toLocaleString(),
    sortValue: (e) => e.signalCount,
  },
  {
    key: "location",
    header: "Est. location",
    render: (e) => locationLabel(e),
  },
  {
    key: "uncertainty",
    header: "± m",
    numeric: true,
    render: (e) => (e.estUncertaintyM === null ? "—" : e.estUncertaintyM.toLocaleString()),
    sortValue: (e) => e.estUncertaintyM ?? -1,
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

export default function Emitters() {
  const { mode } = useColorMode();
  const { data, loading, error } = usePolling(getEmitters, 15000);
  const [selected, setSelected] = useState<Emitter | null>(null);

  const clickColumns: Column<Emitter>[] = columns.map((c) =>
    c.key === "id"
      ? {
          ...c,
          render: (e) => (
            <Box
              component="button"
              onClick={() => setSelected(e)}
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
              {e.id}
            </Box>
          ),
        }
      : c,
  );

  return (
    <ChartCard title="Emitters" legend={<ProtocolLegend />}>
      {loading && !data && <Loading />}
      {error && <ErrorState message={error} />}
      {data && data.length === 0 && (
        <Typography variant="body2" color="text.secondary">
          No emitters to display.
        </Typography>
      )}
      {data && data.length > 0 && (
        <DataTable columns={clickColumns} rows={data} rowKey={(e) => e.id} />
      )}

      <Drawer anchor="right" open={selected !== null} onClose={() => setSelected(null)}>
        <Toolbar />
        <Box sx={{ width: 340, p: 2 }}>
          {selected && (
            <>
              <Box sx={{ display: "flex", alignItems: "center", justifyContent: "space-between" }}>
                <Typography variant="h6">
                  {protocolLabels[selected.protocol.trim().toLowerCase()] ?? selected.protocol}
                </Typography>
                <IconButton size="small" aria-label="Close" onClick={() => setSelected(null)}>
                  <CloseIcon fontSize="small" />
                </IconButton>
              </Box>
              <Typography variant="caption" color="text.secondary">
                {selected.id}
              </Typography>

              <Divider sx={{ my: 1.5 }} />
              <Typography variant="caption" color="text.secondary">
                Confidence
              </Typography>
              <ConfidenceGauge
                value={selected.confidence}
                color={protocolColor(selected.protocol, mode)}
              />

              <Stack spacing={0.75} sx={{ mt: 1.5 }}>
                <DetailRow label="Freq" value={`${(selected.freqCenterHz / 1e6).toFixed(3)} MHz`} />
                <DetailRow label="Signals" value={selected.signalCount.toLocaleString()} />
                <DetailRow
                  label="Uncertainty"
                  value={
                    selected.estUncertaintyM === null
                      ? "—"
                      : `± ${selected.estUncertaintyM.toLocaleString()} m`
                  }
                />
                <DetailRow label="Location" value={locationLabel(selected)} />
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
    </ChartCard>
  );
}
