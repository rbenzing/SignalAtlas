import Box from "@mui/material/Box";
import Typography from "@mui/material/Typography";
import StatusIcon from "./StatusIcon";
import type { SeverityMeta } from "../lib/status";

export interface SeverityLegendProps {
  items: SeverityMeta[];
}

/** Icon + label legend for alert severities — status color paired with text. */
export default function SeverityLegend({ items }: SeverityLegendProps) {
  return (
    <Box sx={{ display: "flex", flexWrap: "wrap", gap: 1.5 }}>
      {items.map((s) => (
        <Box key={s.key} sx={{ display: "flex", alignItems: "center", gap: 0.5 }}>
          <StatusIcon status={s.status} sx={{ fontSize: 16, color: s.color }} />
          <Typography variant="caption" color="text.secondary">
            {s.label}
          </Typography>
        </Box>
      ))}
    </Box>
  );
}
