import { useMemo } from "react";
import Box from "@mui/material/Box";
import Grid from "@mui/material/Grid";
import Typography from "@mui/material/Typography";
import {
  BarChart,
  Bar,
  Cell,
  LabelList,
  ScatterChart,
  Scatter,
  XAxis,
  YAxis,
  ZAxis,
  CartesianGrid,
  Tooltip,
  ResponsiveContainer,
} from "recharts";
import ChartCard from "../components/ChartCard";
import StatTile from "../components/StatTile";
import ProtocolLegend from "../components/ProtocolLegend";
import SeverityLegend from "../components/SeverityLegend";
import ChartTooltip from "../components/ChartTooltip";
import { useColorMode } from "../theme/ColorModeContext";
import { chartTokens, protocolColor, protocolLabels, protocolOrder } from "../theme/palette";
import { severityMeta, severityRank, type SeverityMeta } from "../lib/status";
import { toMhz } from "../lib/spectrum";
import { getSummary, usePolling } from "../api";
import { useLive } from "../live/LiveProvider";

function normProtocol(p: string): string {
  const s = p.trim().toLowerCase();
  if (s === "wifi" || s === "802.11") return "wi-fi";
  if (s === "adsb") return "ads-b";
  if (s === "bluetooth") return "ble";
  return s;
}

interface SigPoint {
  mhz: number;
  conf: number;
  bwHz: number;
  freqHz: number;
  protocol: string;
  label: string;
}

const CHART_H = 280;

export default function Dashboard() {
  const { mode } = useColorMode();
  const t = chartTokens[mode];

  // deviceCount has no live feed; poll the summary for it. Signals/emitters/
  // alerts come from the live-merged store (hub push, polling fallback).
  const summary = usePolling(getSummary, 5000);
  const { signals, emitters, alerts } = useLive();

  const protocolData = useMemo(() => {
    const counts = new Map<string, number>();
    for (const s of signals) {
      const k = normProtocol(s.protocol);
      counts.set(k, (counts.get(k) ?? 0) + 1);
    }
    return protocolOrder
      .filter((k) => counts.has(k))
      .map((k) => ({
        key: k,
        label: protocolLabels[k] ?? k,
        count: counts.get(k) ?? 0,
        color: protocolColor(k, mode),
      }));
  }, [signals, mode]);

  const presentProtocols = useMemo(() => protocolData.map((d) => d.key), [protocolData]);

  const severityData = useMemo(() => {
    const counts = new Map<string, { meta: SeverityMeta; count: number }>();
    for (const a of alerts) {
      const meta = severityMeta(a.severity);
      const cur = counts.get(meta.key);
      if (cur) cur.count += 1;
      else counts.set(meta.key, { meta, count: 1 });
    }
    return [...counts.values()]
      .sort((a, b) => severityRank(a.meta.status) - severityRank(b.meta.status))
      .map((e) => ({ label: e.meta.label, count: e.count, color: e.meta.color, key: e.meta.key }));
  }, [alerts]);

  const severityLegend = useMemo<SeverityMeta[]>(
    () =>
      severityData.map((d) => ({
        key: d.key,
        label: d.label,
        color: d.color,
        status: severityMeta(d.key).status,
      })),
    [severityData],
  );

  const scatterGroups = useMemo(() => {
    const groups = new Map<string, SigPoint[]>();
    for (const s of signals) {
      const k = normProtocol(s.protocol);
      const pt: SigPoint = {
        mhz: toMhz(s.centerFreqHz),
        conf: s.confidence,
        bwHz: s.bandwidthHz,
        freqHz: s.centerFreqHz,
        protocol: k,
        label: protocolLabels[k] ?? k,
      };
      const arr = groups.get(k);
      if (arr) arr.push(pt);
      else groups.set(k, [pt]);
    }
    return protocolOrder
      .filter((k) => groups.has(k))
      .map((k) => ({
        key: k,
        label: protocolLabels[k] ?? k,
        color: protocolColor(k, mode),
        points: groups.get(k) ?? [],
      }));
  }, [signals, mode]);

  const axisTick = { fill: t.axis, fontSize: 12 };

  return (
    <Box sx={{ display: "flex", flexDirection: "column", gap: 3 }}>
      <Box sx={{ display: "flex", gap: 2, flexWrap: "wrap" }}>
        <StatTile label="Signals" value={signals.length || (summary.data?.signalCount ?? "—")} />
        <StatTile label="Devices" value={summary.data?.deviceCount ?? "—"} />
        <StatTile label="Emitters" value={emitters.length || "—"} />
        <StatTile label="Alerts" value={alerts.length || (summary.data?.alertCount ?? "—")} />
      </Box>

      <Grid container spacing={3}>
        <Grid size={{ xs: 12, md: 6 }}>
          <ChartCard title="Protocol distribution" legend={<ProtocolLegend protocols={presentProtocols} />}>
            {protocolData.length === 0 && (
              <Typography variant="body2" color="text.secondary">
                No signals to display.
              </Typography>
            )}
            {protocolData.length > 0 && (
              <ResponsiveContainer width="100%" height={CHART_H}>
                <BarChart layout="vertical" data={protocolData} margin={{ left: 8, right: 32 }}>
                  <CartesianGrid horizontal={false} stroke={t.grid} />
                  <XAxis type="number" allowDecimals={false} stroke={t.axis} tick={axisTick} />
                  <YAxis
                    type="category"
                    dataKey="label"
                    width={64}
                    stroke={t.axis}
                    tick={axisTick}
                  />
                  <Tooltip
                    cursor={{ fill: t.grid, opacity: 0.3 }}
                    content={({ active, payload }) =>
                      active && payload?.length ? (
                        <ChartTooltip>
                          <Typography variant="caption" sx={{ fontWeight: 600 }}>
                            {payload[0].payload.label}
                          </Typography>
                          <Typography variant="body2">{payload[0].payload.count} signals</Typography>
                        </ChartTooltip>
                      ) : null
                    }
                  />
                  <Bar dataKey="count" radius={[0, 3, 3, 0]}>
                    {protocolData.map((d) => (
                      <Cell key={d.key} fill={d.color} />
                    ))}
                    <LabelList dataKey="count" position="right" fill={t.ink} fontSize={12} />
                  </Bar>
                </BarChart>
              </ResponsiveContainer>
            )}
          </ChartCard>
        </Grid>

        <Grid size={{ xs: 12, md: 6 }}>
          <ChartCard title="Alerts by severity" legend={<SeverityLegend items={severityLegend} />}>
            {severityData.length === 0 && (
              <Typography variant="body2" color="text.secondary">
                No active alerts.
              </Typography>
            )}
            {severityData.length > 0 && (
              <ResponsiveContainer width="100%" height={CHART_H}>
                <BarChart data={severityData} margin={{ left: 0, right: 8, top: 16 }}>
                  <CartesianGrid vertical={false} stroke={t.grid} />
                  <XAxis dataKey="label" stroke={t.axis} tick={axisTick} />
                  <YAxis allowDecimals={false} stroke={t.axis} tick={axisTick} />
                  <Tooltip
                    cursor={{ fill: t.grid, opacity: 0.3 }}
                    content={({ active, payload }) =>
                      active && payload?.length ? (
                        <ChartTooltip>
                          <Typography variant="caption" sx={{ fontWeight: 600 }}>
                            {payload[0].payload.label}
                          </Typography>
                          <Typography variant="body2">{payload[0].payload.count} alerts</Typography>
                        </ChartTooltip>
                      ) : null
                    }
                  />
                  <Bar dataKey="count" radius={[3, 3, 0, 0]}>
                    {severityData.map((d) => (
                      <Cell key={d.key} fill={d.color} />
                    ))}
                    <LabelList dataKey="count" position="top" fill={t.ink} fontSize={12} />
                  </Bar>
                </BarChart>
              </ResponsiveContainer>
            )}
          </ChartCard>
        </Grid>

        <Grid size={{ xs: 12 }}>
          <ChartCard
            title="Signal frequency landscape"
            legend={<ProtocolLegend protocols={presentProtocols} />}
          >
            {scatterGroups.length === 0 && (
              <Typography variant="body2" color="text.secondary">
                No signals to display.
              </Typography>
            )}
            {scatterGroups.length > 0 && (
              <ResponsiveContainer width="100%" height={360}>
                <ScatterChart margin={{ left: 8, right: 24, top: 8, bottom: 16 }}>
                  <CartesianGrid stroke={t.grid} />
                  <XAxis
                    type="number"
                    dataKey="mhz"
                    name="Frequency"
                    unit=" MHz"
                    domain={["auto", "auto"]}
                    stroke={t.axis}
                    tick={axisTick}
                  />
                  <YAxis
                    type="number"
                    dataKey="conf"
                    name="Confidence"
                    domain={[0, 1]}
                    stroke={t.axis}
                    tick={axisTick}
                  />
                  <ZAxis type="number" dataKey="bwHz" range={[30, 400]} name="Bandwidth" />
                  <Tooltip
                    cursor={{ strokeDasharray: "3 3", stroke: t.axis }}
                    content={({ active, payload }) => {
                      if (!active || !payload?.length) return null;
                      const p = payload[0].payload as SigPoint;
                      return (
                        <ChartTooltip>
                          <Typography variant="caption" sx={{ fontWeight: 600 }}>
                            {p.label}
                          </Typography>
                          <Typography variant="body2">{p.mhz.toFixed(3)} MHz</Typography>
                          <Typography variant="body2">
                            BW {(p.bwHz / 1e3).toFixed(1)} kHz
                          </Typography>
                          <Typography variant="body2">
                            Confidence {(p.conf * 100).toFixed(0)}%
                          </Typography>
                        </ChartTooltip>
                      );
                    }}
                  />
                  {scatterGroups.map((g) => (
                    <Scatter key={g.key} name={g.label} data={g.points} fill={g.color} fillOpacity={0.75} />
                  ))}
                </ScatterChart>
              </ResponsiveContainer>
            )}
          </ChartCard>
        </Grid>
      </Grid>
    </Box>
  );
}
