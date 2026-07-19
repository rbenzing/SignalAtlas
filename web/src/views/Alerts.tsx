import { useMemo, useState } from "react";
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
import StatusIcon from "../components/StatusIcon";
import SeverityLegend from "../components/SeverityLegend";
import { severityMeta, severityRank, type SeverityMeta } from "../lib/status";
import { type Alert } from "../api";
import { useLive } from "../live/LiveProvider";

function SeverityCell({ severity }: { severity: string }) {
  const meta = severityMeta(severity);
  return (
    <Box sx={{ display: "flex", alignItems: "center", gap: 0.75 }}>
      <StatusIcon status={meta.status} sx={{ fontSize: 18, color: meta.color }} />
      <Typography variant="body2">{meta.label}</Typography>
    </Box>
  );
}

const columns: Column<Alert>[] = [
  {
    key: "severity",
    header: "Severity",
    render: (a) => <SeverityCell severity={a.severity} />,
    sortValue: (a) => severityRank(severityMeta(a.severity).status),
  },
  { key: "kind", header: "Kind", render: (a) => a.kind, sortValue: (a) => a.kind },
  { key: "summary", header: "Summary", render: (a) => a.summary },
  { key: "time", header: "Time", render: (a) => a.time, sortValue: (a) => a.time },
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

export default function Alerts() {
  // Live-merged alerts (hub push prepends newest; polling fallback when offline).
  const { alerts: data } = useLive();
  const [selected, setSelected] = useState<Alert | null>(null);

  // Severity-sorted (most severe first) and legend of present severities.
  const sorted = useMemo(
    () =>
      [...(data ?? [])].sort(
        (a, b) =>
          severityRank(severityMeta(a.severity).status) -
          severityRank(severityMeta(b.severity).status),
      ),
    [data],
  );

  const legend = useMemo<SeverityMeta[]>(() => {
    const seen = new Map<string, SeverityMeta>();
    for (const a of data ?? []) {
      const m = severityMeta(a.severity);
      if (!seen.has(m.key)) seen.set(m.key, m);
    }
    return [...seen.values()].sort((a, b) => severityRank(a.status) - severityRank(b.status));
  }, [data]);

  const clickColumns: Column<Alert>[] = columns.map((c) =>
    c.key === "kind"
      ? {
          ...c,
          render: (a) => (
            <Box
              component="button"
              onClick={() => setSelected(a)}
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
              {a.kind}
            </Box>
          ),
        }
      : c,
  );

  return (
    <ChartCard title="Alerts" legend={<SeverityLegend items={legend} />}>
      {data.length === 0 && (
        <Typography variant="body2" color="text.secondary">
          No active alerts.
        </Typography>
      )}
      {data.length > 0 && (
        <DataTable columns={clickColumns} rows={sorted} rowKey={(a) => a.id} />
      )}

      <Drawer anchor="right" open={selected !== null} onClose={() => setSelected(null)}>
        <Toolbar />
        <Box sx={{ width: 360, p: 2 }}>
          {selected && (
            <>
              <Box sx={{ display: "flex", alignItems: "center", justifyContent: "space-between" }}>
                <SeverityCell severity={selected.severity} />
                <IconButton size="small" aria-label="Close" onClick={() => setSelected(null)}>
                  <CloseIcon fontSize="small" />
                </IconButton>
              </Box>

              <Divider sx={{ my: 1.5 }} />
              <Stack spacing={0.75}>
                <DetailRow label="Kind" value={selected.kind} />
                <DetailRow label="Time" value={selected.time} />
              </Stack>
              <Typography variant="body2" sx={{ mt: 1 }}>
                {selected.summary}
              </Typography>

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
