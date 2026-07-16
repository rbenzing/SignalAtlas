import { createTheme, type Theme } from "@mui/material/styles";
import { chartTokens, statusPalette, type ColorMode } from "./palette";

const SYSTEM_FONT =
  'system-ui, -apple-system, "Segoe UI", Roboto, Helvetica, Arial, sans-serif';

/** Build a MUI theme for the given color mode using the locked chart tokens. */
export function buildTheme(mode: ColorMode): Theme {
  const t = chartTokens[mode];
  return createTheme({
    palette: {
      mode,
      background: { default: t.surfaceAlt, paper: t.surface },
      text: { primary: t.ink },
      divider: t.grid,
      success: { main: statusPalette.good },
      warning: { main: statusPalette.warning },
      error: { main: statusPalette.critical },
    },
    typography: {
      fontFamily: SYSTEM_FONT,
      h1: { fontSize: "1.6rem", fontWeight: 600 },
      h2: { fontSize: "1.3rem", fontWeight: 600 },
    },
    shape: { borderRadius: 8 },
    components: {
      MuiPaper: { defaultProps: { elevation: 0 } },
    },
  });
}
