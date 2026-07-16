import Box from "@mui/material/Box";
import Typography from "@mui/material/Typography";
import StatusIcon from "./StatusIcon";
import { formatMhz } from "../lib/spectrum";
import { coverageMeta, formatAge } from "../lib/status";
import type { SpectrumCoverageBand } from "../api";

export interface CoverageStripProps {
  bands: SpectrumCoverageBand[];
}

/**
 * One row per priority band, colored by coverage freshness using the reserved
 * STATUS palette with an icon + text label + last-seen age (never color alone).
 */
export default function CoverageStrip({ bands }: CoverageStripProps) {
  if (bands.length === 0) {
    return (
      <Typography variant="body2" color="text.secondary">
        No coverage data.
      </Typography>
    );
  }
  return (
    <Box sx={{ display: "flex", flexDirection: "column", gap: 1 }}>
      {bands.map((b) => {
        const meta = coverageMeta(b.covered, b.ageSeconds);
        const statusColor =
          meta.status === "good"
            ? "success.main"
            : meta.status === "warning"
              ? "warning.main"
              : "error.main";
        return (
          <Box
            key={b.key}
            sx={{
              display: "flex",
              alignItems: "center",
              gap: 1.5,
              px: 1.5,
              py: 1,
              border: 1,
              borderColor: "divider",
              borderRadius: 1,
              bgcolor: "background.default",
            }}
          >
            <StatusIcon status={meta.status} sx={{ color: statusColor, fontSize: 22 }} />
            <Box sx={{ minWidth: 150 }}>
              <Typography variant="body2" sx={{ fontWeight: 600 }}>
                {b.label}
              </Typography>
              <Typography variant="caption" color="text.secondary">
                {formatMhz(b.lowHz, 1)}–{formatMhz(b.highHz, 1)} MHz
              </Typography>
            </Box>
            <Box sx={{ ml: "auto", textAlign: "right" }}>
              <Typography variant="body2" sx={{ fontWeight: 600 }}>
                {meta.label}
              </Typography>
              <Typography variant="caption" color="text.secondary">
                {formatAge(b.ageSeconds)}
              </Typography>
            </Box>
          </Box>
        );
      })}
    </Box>
  );
}
