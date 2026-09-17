import Paper from "@mui/material/Paper";
import Typography from "@mui/material/Typography";
import Box from "@mui/material/Box";

export interface StatTileProps {
  label: string;
  value: React.ReactNode;
  /** Optional signed delta (e.g. "+3", "-1.2%"); colored by sign. */
  delta?: string;
}

/**
 * Compact KPI tile for the dashboard header row.
 *
 * a11y: the tile is a labelled `group`, NOT a heading. The value used to render as an `h5`, which
 * (a) skipped from the page `h1` straight to `h5` — a Lighthouse `heading-order` failure — and
 * (b) put a bare number in the heading outline, so navigating by heading announced "0, 0, 0, 0"
 * with the labels stranded in separate text. The group's accessible name pairs the two
 * ("Emitters: 0"), and `aria-live="polite"` announces changes as live RF data arrives, which is the
 * whole point of these tiles in a monitoring tool.
 */
export default function StatTile({ label, value, delta }: StatTileProps) {
  const deltaColor = delta?.startsWith("-") ? "error.main" : "success.main";
  const spoken = `${label}: ${value}${delta ? ` (${delta})` : ""}`;
  return (
    <Paper
      variant="outlined"
      role="group"
      aria-label={spoken}
      aria-live="polite"
      sx={{ p: 2, minWidth: 140, display: "flex", flexDirection: "column", gap: 0.5 }}
    >
      <Typography variant="caption" color="text.secondary" aria-hidden>
        {label}
      </Typography>
      <Box sx={{ display: "flex", alignItems: "baseline", gap: 1 }} aria-hidden>
        {/* variant keeps the type scale; component="p" keeps it out of the heading outline. */}
        <Typography variant="h5" component="p" sx={{ fontVariantNumeric: "tabular-nums" }}>
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
