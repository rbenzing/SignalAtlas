import Paper from "@mui/material/Paper";

/** Themed container for custom Recharts tooltips (surface + ink via theme). */
export default function ChartTooltip({ children }: { children: React.ReactNode }) {
  return (
    <Paper
      variant="outlined"
      sx={{ px: 1.25, py: 0.75, bgcolor: "background.paper", boxShadow: 3 }}
    >
      {children}
    </Paper>
  );
}
