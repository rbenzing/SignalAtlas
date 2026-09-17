import Box from "@mui/material/Box";
import Tooltip from "@mui/material/Tooltip";
import { getHealth, usePolling } from "../api";
import { statusPalette } from "../theme/palette";
import { useLive } from "../live/LiveProvider";

const GREY = "#898781";

/**
 * Live connection indicator. Primary signal is the SignalR hub state
 * (connected=green, reconnecting=amber, disconnected/polling=grey); the ungated
 * /health probe is a secondary hint shown in the tooltip.
 */
export default function ConnectionDot() {
  const { status } = useLive();
  const { data } = usePolling(getHealth, 5000);
  const apiOnline = data === true;

  const color =
    status === "connected"
      ? statusPalette.good
      : status === "reconnecting"
        ? statusPalette.warning
        : GREY;

  const hubLabel =
    status === "connected"
      ? "Live hub connected"
      : status === "reconnecting"
        ? "Live hub reconnecting…"
        : "Live hub offline — polling";

  const label = `${hubLabel} · API ${apiOnline ? "online" : "unreachable"}`;

  // a11y: `aria-label` is PROHIBITED on a bare div (no role), so the label was silently dropped and
  // connection state was conveyed by colour alone with no accessible equivalent — a Lighthouse
  // `aria-prohibited-attr` failure. `role="status"` makes the name valid AND announces transitions
  // (hub drops to polling, API goes unreachable), which a monitoring tool has to surface non-visually.
  return (
    <Tooltip title={label}>
      <Box
        role="status"
        aria-label={label}
        sx={{ width: 10, height: 10, borderRadius: "50%", bgcolor: color, flexShrink: 0 }}
      />
    </Tooltip>
  );
}
