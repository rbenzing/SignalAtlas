import Paper from "@mui/material/Paper";
import Typography from "@mui/material/Typography";
import Box from "@mui/material/Box";

export interface ChartCardProps {
  title: string;
  /** Optional legend / control slot rendered top-right of the header. */
  legend?: React.ReactNode;
  children?: React.ReactNode;
}

/** Themed surface card wrapping a chart, table, or view stub. */
export default function ChartCard({ title, legend, children }: ChartCardProps) {
  return (
    <Paper variant="outlined" sx={{ p: 2, bgcolor: "background.paper" }}>
      <Box
        sx={{
          display: "flex",
          alignItems: "center",
          justifyContent: "space-between",
          gap: 2,
          mb: 1.5,
          flexWrap: "wrap",
        }}
      >
        <Typography variant="h2" sx={{ fontSize: "1.15rem" }}>
          {title}
        </Typography>
        {legend && <Box>{legend}</Box>}
      </Box>
      {children}
    </Paper>
  );
}
