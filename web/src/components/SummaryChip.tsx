import Chip from "@mui/material/Chip";
import { getSummary, usePolling } from "../api";

/** Live summary counts chip (signals / devices / alerts) for the AppBar. */
export default function SummaryChip() {
  const { data } = usePolling(getSummary, 10000);
  const label = data
    ? `${data.signalCount} sig · ${data.deviceCount} dev · ${data.alertCount} alerts`
    : "— sig · — dev · — alerts";
  return <Chip size="small" variant="outlined" label={label} />;
}
