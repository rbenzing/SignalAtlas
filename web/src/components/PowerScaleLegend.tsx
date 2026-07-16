import Box from "@mui/material/Box";
import Typography from "@mui/material/Typography";
import { powerGradientCss } from "../lib/spectrum";

export interface PowerScaleLegendProps {
  min: number;
  max: number;
  height: number;
}

/** Vertical dBFS color-scale legend for the waterfall (low → high power). */
export default function PowerScaleLegend({ min, max, height }: PowerScaleLegendProps) {
  const mid = (min + max) / 2;
  return (
    <Box sx={{ display: "flex", flexDirection: "column", alignItems: "center", gap: 0.5 }}>
      <Typography variant="caption" color="text.secondary">
        dBFS
      </Typography>
      <Box sx={{ display: "flex", gap: 0.5, height }}>
        <Box
          aria-hidden
          sx={{
            width: 14,
            borderRadius: "2px",
            background: powerGradientCss,
            border: 1,
            borderColor: "divider",
          }}
        />
        <Box sx={{ display: "flex", flexDirection: "column", justifyContent: "space-between" }}>
          <Typography variant="caption" color="text.secondary">
            {max.toFixed(0)}
          </Typography>
          <Typography variant="caption" color="text.secondary">
            {mid.toFixed(0)}
          </Typography>
          <Typography variant="caption" color="text.secondary">
            {min.toFixed(0)}
          </Typography>
        </Box>
      </Box>
    </Box>
  );
}
