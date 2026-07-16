import Box from "@mui/material/Box";
import Typography from "@mui/material/Typography";
import type { EvidenceItem } from "../api";

export interface EvidenceListProps {
  items: EvidenceItem[];
}

/**
 * Renders evidence as `feature=value·weight`. All text is rendered as React
 * children (auto-escaped) — never dangerouslySetInnerHTML — to neutralize any
 * RF-string injection in operator-facing fields (review R2).
 */
export default function EvidenceList({ items }: EvidenceListProps) {
  if (!items?.length) {
    return (
      <Typography variant="caption" color="text.secondary">
        —
      </Typography>
    );
  }
  return (
    <Box component="ul" sx={{ m: 0, pl: 0, listStyle: "none" }}>
      {items.map((e, i) => (
        <Box component="li" key={`${e.feature}-${i}`} sx={{ display: "flex", gap: 0.5 }}>
          <Typography variant="caption" sx={{ fontVariantNumeric: "tabular-nums" }}>
            {e.feature}={e.value}
            <Typography component="span" variant="caption" color="text.secondary">
              {" "}
              ·{e.weight.toFixed(2)}
            </Typography>
          </Typography>
        </Box>
      ))}
    </Box>
  );
}
