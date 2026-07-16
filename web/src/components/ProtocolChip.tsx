import Chip from "@mui/material/Chip";
import { useColorMode } from "../theme/ColorModeContext";
import { protocolColor, protocolLabels } from "../theme/palette";

export interface ProtocolChipProps {
  protocol: string;
}

/** Small chip labeled + colored by protocol (Unknown → neutral gray). */
export default function ProtocolChip({ protocol }: ProtocolChipProps) {
  const { mode } = useColorMode();
  const color = protocolColor(protocol, mode);
  const label = protocolLabels[protocol.trim().toLowerCase()] ?? protocol;
  return (
    <Chip
      size="small"
      label={label}
      variant="outlined"
      sx={{
        borderColor: color,
        color: "text.primary",
        "& .MuiChip-label": { fontWeight: 600 },
        "&::before": {
          content: '""',
          display: "inline-block",
          width: 8,
          height: 8,
          borderRadius: "50%",
          bgcolor: color,
          ml: 1,
        },
      }}
    />
  );
}
