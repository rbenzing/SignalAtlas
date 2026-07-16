import Box from "@mui/material/Box";
import CircularProgress from "@mui/material/CircularProgress";
import Typography from "@mui/material/Typography";

export interface LoadingProps {
  label?: string;
}

/** Centered spinner for pending fetches. */
export default function Loading({ label = "Loading…" }: LoadingProps) {
  return (
    <Box sx={{ display: "flex", alignItems: "center", gap: 1.5, p: 3 }}>
      <CircularProgress size={20} />
      <Typography variant="body2" color="text.secondary">
        {label}
      </Typography>
    </Box>
  );
}
