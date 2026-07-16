import Paper from "@mui/material/Paper";
import Typography from "@mui/material/Typography";
import Box from "@mui/material/Box";

export interface StatTileProps {
  label: string;
  value: React.ReactNode;
  /** Optional signed delta (e.g. "+3", "-1.2%"); colored by sign. */
  delta?: string;
}

/** Compact KPI tile for the dashboard header row. */
export default function StatTile({ label, value, delta }: StatTileProps) {
  const deltaColor = delta?.startsWith("-") ? "error.main" : "success.main";
  return (
    <Paper
      variant="outlined"
      sx={{ p: 2, minWidth: 140, display: "flex", flexDirection: "column", gap: 0.5 }}
    >
      <Typography variant="caption" color="text.secondary">
        {label}
      </Typography>
      <Box sx={{ display: "flex", alignItems: "baseline", gap: 1 }}>
        <Typography variant="h5" sx={{ fontVariantNumeric: "tabular-nums" }}>
          {value}
        </Typography>
        {delta && (
          <Typography variant="body2" sx={{ color: deltaColor }}>
            {delta}
          </Typography>
        )}
      </Box>
    </Paper>
  );
}
