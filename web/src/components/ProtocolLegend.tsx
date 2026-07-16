import Box from "@mui/material/Box";
import Typography from "@mui/material/Typography";
import { useColorMode } from "../theme/ColorModeContext";
import { protocolColor, protocolLabels, protocolOrder } from "../theme/palette";

export interface ProtocolLegendProps {
  /** Subset of protocol keys to show; defaults to the full known set. */
  protocols?: readonly string[];
}

/** Swatch legend for protocol categorical colors — always shown beside charts. */
export default function ProtocolLegend({ protocols = protocolOrder }: ProtocolLegendProps) {
  const { mode } = useColorMode();
  return (
    <Box sx={{ display: "flex", flexWrap: "wrap", gap: 1.5 }}>
      {protocols.map((p) => (
        <Box key={p} sx={{ display: "flex", alignItems: "center", gap: 0.5 }}>
          <Box
            sx={{
              width: 12,
              height: 12,
              borderRadius: "2px",
              bgcolor: protocolColor(p, mode),
            }}
          />
          <Typography variant="caption" color="text.secondary">
            {protocolLabels[p] ?? p}
          </Typography>
        </Box>
      ))}
    </Box>
  );
}
