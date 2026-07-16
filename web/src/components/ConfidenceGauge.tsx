import Box from "@mui/material/Box";
import Typography from "@mui/material/Typography";

export interface ConfidenceGaugeProps {
  /** Confidence in [0,1]. */
  value: number;
  color: string;
}

/** Tiny horizontal confidence bar with a tabular-nums percentage label. */
export default function ConfidenceGauge({ value, color }: ConfidenceGaugeProps) {
  const pct = Math.max(0, Math.min(1, value)) * 100;
  return (
    <Box sx={{ display: "flex", alignItems: "center", gap: 1 }}>
      <Box
        sx={{
          position: "relative",
          flex: 1,
          height: 8,
          borderRadius: 4,
          bgcolor: "action.hover",
          overflow: "hidden",
        }}
      >
        <Box
          sx={{
            position: "absolute",
            inset: 0,
            width: `${pct}%`,
            bgcolor: color,
            borderRadius: 4,
          }}
        />
      </Box>
      <Typography variant="body2" sx={{ fontVariantNumeric: "tabular-nums", minWidth: 40, textAlign: "right" }}>
        {pct.toFixed(0)}%
      </Typography>
    </Box>
  );
}
