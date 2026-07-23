import { useMemo } from "react";
import Box from "@mui/material/Box";
import Grid from "@mui/material/Grid";
import Typography from "@mui/material/Typography";
import {
  AreaChart,
  Area,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  ReferenceLine,
  ResponsiveContainer,
} from "recharts";
import ChartCard from "../components/ChartCard";
import Loading from "../components/Loading";
import ErrorState from "../components/ErrorState";
import Waterfall from "../components/Waterfall";
import PowerScaleLegend from "../components/PowerScaleLegend";
import AudioPlayer from "../components/AudioPlayer";
import ChartTooltip from "../components/ChartTooltip";
import { useColorMode } from "../theme/ColorModeContext";
import { chartTokens, powerRamp } from "../theme/palette";
import { powerRange, toMhz } from "../lib/spectrum";
import { getSpectrumFrames, getSpectrumOccupancy, usePolling } from "../api";
import { useLive } from "../live/LiveProvider";

const WATERFALL_H = 320;
const OCC_COLOR = powerRamp[4]; // on-brand blue; power spectrum is a single metric, not a protocol

export default function LiveSpectrum() {
  const { mode } = useColorMode();
  const t = chartTokens[mode];

  const frames = usePolling(getSpectrumFrames, 1500);
  const occupancy = usePolling(getSpectrumOccupancy, 2000);

  // Prefer the live push buffer (animates in real time); fall back to polled
  // frames when the hub buffer is empty.
  const { spectrumFrames: liveFrames } = useLive();
  const displayFrames = liveFrames.length > 0 ? liveFrames : frames.data ?? [];

  const range = useMemo(() => powerRange(displayFrames), [displayFrames]);

  const occData = useMemo(() => {
    const o = occupancy.data;
    if (!o) return [];
    return o.freqHz.map((hz, i) => ({ mhz: toMhz(hz), dbfs: o.powerDbfs[i] ?? 0 }));
  }, [occupancy.data]);

  const axisTick = { fill: t.axis, fontSize: 12 };
  const hasFrames = displayFrames.length > 0;

  return (
    <Box sx={{ display: "flex", flexDirection: "column", gap: 3 }}>
      <ChartCard title="Waterfall — frequency rainfall">
        {frames.loading && !frames.data && !hasFrames && <Loading />}
        {frames.error && !hasFrames && <ErrorState message={frames.error} />}
        {frames.data && !hasFrames && (
          <Typography variant="body2" color="text.secondary">
            No spectrum frames available.
          </Typography>
        )}
        {hasFrames && (
          <Box sx={{ display: "flex", gap: 2, alignItems: "flex-start" }}>
            <Box sx={{ flex: 1, minWidth: 0 }}>
              <Waterfall frames={displayFrames} min={range.min} max={range.max} height={WATERFALL_H} />
            </Box>
            <PowerScaleLegend min={range.min} max={range.max} height={WATERFALL_H} />
          </Box>
        )}
      </ChartCard>

      <Grid container spacing={3}>
        <Grid size={{ xs: 12, md: 8 }}>
          <ChartCard title="Power spectrum vs frequency">
            {occupancy.loading && !occupancy.data && <Loading />}
            {occupancy.error && <ErrorState message={occupancy.error} />}
            {occupancy.data && occData.length === 0 && (
              <Typography variant="body2" color="text.secondary">
                No spectrum data.
              </Typography>
            )}
            {occData.length > 0 && occupancy.data && (
              <>
                <ResponsiveContainer width="100%" height={300}>
                  <AreaChart data={occData} margin={{ left: 0, right: 16, top: 8, bottom: 8 }}>
                    <CartesianGrid stroke={t.grid} />
                    <XAxis
                      type="number"
                      dataKey="mhz"
                      domain={["auto", "auto"]}
                      unit=" MHz"
                      stroke={t.axis}
                      tick={axisTick}
                    />
                    <YAxis
                      domain={["auto", "auto"]}
                      unit=" dBFS"
                      width={64}
                      stroke={t.axis}
                      tick={axisTick}
                    />
                    <Tooltip
                      cursor={{ stroke: t.axis }}
                      content={({ active, payload }) =>
                        active && payload?.length ? (
                          <ChartTooltip>
                            <Typography variant="body2">
                              {(payload[0].payload.mhz as number).toFixed(3)} MHz
                            </Typography>
                            <Typography variant="body2">
                              {(payload[0].payload.dbfs as number).toFixed(1)} dBFS
                            </Typography>
                          </ChartTooltip>
                        ) : null
                      }
                    />
                    <ReferenceLine
                      y={occupancy.data.thresholdDbfs}
                      stroke={t.axis}
                      strokeDasharray="4 4"
                      label={{
                        value: `threshold ${occupancy.data.thresholdDbfs.toFixed(0)} dBFS`,
                        position: "insideTopRight",
                        fill: t.axis,
                        fontSize: 11,
                      }}
                    />
                    <Area
                      type="monotone"
                      dataKey="dbfs"
                      stroke={OCC_COLOR}
                      strokeWidth={1.5}
                      fill={OCC_COLOR}
                      fillOpacity={0.22}
                      isAnimationActive={false}
                    />
                  </AreaChart>
                </ResponsiveContainer>
                <Typography variant="caption" color="text.secondary">
                  Occupied {(occupancy.data.occupiedFraction * 100).toFixed(0)}% of band
                  (bins above threshold)
                </Typography>
              </>
            )}
          </ChartCard>
        </Grid>

        <Grid size={{ xs: 12, md: 4 }}>
          <AudioPlayer />
        </Grid>
      </Grid>
    </Box>
  );
}
