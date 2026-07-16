import ErrorIcon from "@mui/icons-material/Error";
import ReportProblemIcon from "@mui/icons-material/ReportProblem";
import WarningAmberIcon from "@mui/icons-material/WarningAmber";
import CheckCircleIcon from "@mui/icons-material/CheckCircle";
import type { SvgIconProps } from "@mui/material/SvgIcon";
import type { StatusKey } from "../theme/palette";

export interface StatusIconProps extends SvgIconProps {
  status: StatusKey;
}

/** Icon for a status/severity — pairs with a text label so color is never alone. */
export default function StatusIcon({ status, ...props }: StatusIconProps) {
  switch (status) {
    case "critical":
      return <ErrorIcon {...props} />;
    case "serious":
      return <ReportProblemIcon {...props} />;
    case "warning":
      return <WarningAmberIcon {...props} />;
    case "good":
    default:
      return <CheckCircleIcon {...props} />;
  }
}
