import { useState } from "react";
import { Link as RouterLink, useLocation } from "react-router-dom";
import AppBar from "@mui/material/AppBar";
import Toolbar from "@mui/material/Toolbar";
import Typography from "@mui/material/Typography";
import Drawer from "@mui/material/Drawer";
import Box from "@mui/material/Box";
import List from "@mui/material/List";
import ListItem from "@mui/material/ListItem";
import ListItemButton from "@mui/material/ListItemButton";
import ListItemIcon from "@mui/material/ListItemIcon";
import ListItemText from "@mui/material/ListItemText";
import IconButton from "@mui/material/IconButton";
import Tooltip from "@mui/material/Tooltip";
import useMediaQuery from "@mui/material/useMediaQuery";
import { useTheme } from "@mui/material/styles";
import DashboardIcon from "@mui/icons-material/Dashboard";
import GraphicEqIcon from "@mui/icons-material/GraphicEq";
import MapIcon from "@mui/icons-material/Map";
import CellTowerIcon from "@mui/icons-material/CellTower";
import DevicesIcon from "@mui/icons-material/Devices";
import NotificationsIcon from "@mui/icons-material/Notifications";
import PsychologyIcon from "@mui/icons-material/Psychology";
import MenuIcon from "@mui/icons-material/Menu";
import DarkModeIcon from "@mui/icons-material/DarkMode";
import LightModeIcon from "@mui/icons-material/LightMode";
import { useColorMode } from "../theme/ColorModeContext";
import { useSdr } from "../sdr/SdrProvider";
import { isMappingBand } from "../sdr/bandPresets";
import ConnectionDot from "./ConnectionDot";
import SummaryChip from "./SummaryChip";
import HackRfConnect from "./HackRfConnect";

const DRAWER_WIDTH = 220;

const NAV = [
  { label: "Dashboard", path: "/", icon: <DashboardIcon /> },
  { label: "Live Spectrum", path: "/spectrum", icon: <GraphicEqIcon /> },
  // Mapping-gated: the RF Map has no use (no contacts to plot) unless the tuned frequency is
  // ADS-B 1090 MHz or NOAA APT 137 MHz — see `mapOnly` handling below.
  { label: "RF Map", path: "/map", icon: <MapIcon />, mapOnly: true },
  { label: "Emitters", path: "/emitters", icon: <CellTowerIcon /> },
  { label: "Devices", path: "/devices", icon: <DevicesIcon /> },
  { label: "Alerts", path: "/alerts", icon: <NotificationsIcon /> },
  { label: "Analyst", path: "/analyst", icon: <PsychologyIcon /> },
];

export default function AppShell({ children }: { children: React.ReactNode }) {
  const theme = useTheme();
  const { mode, toggle } = useColorMode();
  const { activeFreqHz } = useSdr();
  const isDesktop = useMediaQuery(theme.breakpoints.up("md"));
  const [mobileOpen, setMobileOpen] = useState(false);
  const location = useLocation();
  const mapEnabled = isMappingBand(activeFreqHz);

  const navList = (
    <List>
      {NAV.map((item) => {
        const disabled = Boolean(item.mapOnly) && !mapEnabled;
        return (
          <ListItem key={item.path} disablePadding>
            <Tooltip
              title={disabled ? "Available when tuned to a mapping band (e.g. ADS-B 1090 MHz)" : ""}
              disableHoverListener={!disabled}
            >
              {/*
                Unavailable items stay FOCUSABLE. `disabled` removes them from the tab order, so a
                keyboard or screen-reader user could never reach the control to learn WHY it is
                unavailable. `aria-disabled` conveys the same state while keeping it reachable, and
                the tooltip (shown on focus as well as hover) supplies the reason. Navigation is
                still suppressed by omitting RouterLink/`to`.
              */}
              <ListItemButton
                {...(disabled ? {} : { component: RouterLink, to: item.path })}
                aria-disabled={disabled || undefined}
                selected={location.pathname === item.path}
                onClick={() => !disabled && setMobileOpen(false)}
                sx={{
                  ...(disabled && { opacity: 0.5, cursor: "not-allowed" }),
                  // a11y (WCAG 1.4.11): the default focus style here was a 12% white wash measuring
                  // ~1.27:1 against the sidebar — well under the 3:1 minimum, and nearly identical
                  // to the `selected` background, so focus and selection were indistinguishable.
                  "&.Mui-focusVisible": {
                    outline: "2px solid",
                    outlineColor: "primary.main",
                    outlineOffset: "-2px",
                  },
                }}
              >
                <ListItemIcon sx={{ minWidth: 40 }}>{item.icon}</ListItemIcon>
                <ListItemText primary={item.label} />
              </ListItemButton>
            </Tooltip>
          </ListItem>
        );
      })}
    </List>
  );

  return (
    <Box sx={{ display: "flex", minHeight: "100vh" }}>
      <AppBar position="fixed" color="default" elevation={0} sx={{ borderBottom: 1, borderColor: "divider", zIndex: (t) => t.zIndex.drawer + 1 }}>
        <Toolbar sx={{ gap: 2 }}>
          {!isDesktop && (
            <IconButton edge="start" onClick={() => setMobileOpen((o) => !o)} aria-label="Toggle navigation">
              <MenuIcon />
            </IconButton>
          )}
          <Typography variant="h1" sx={{ fontSize: "1.25rem", flexGrow: 1 }}>
            Signal Atlas
          </Typography>
          <SummaryChip />
          <HackRfConnect />
          <ConnectionDot />
          <IconButton onClick={toggle} aria-label="Toggle color mode">
            {mode === "dark" ? <LightModeIcon /> : <DarkModeIcon />}
          </IconButton>
        </Toolbar>
      </AppBar>

      <Box component="nav" sx={{ width: { md: DRAWER_WIDTH }, flexShrink: { md: 0 } }}>
        <Drawer
          variant={isDesktop ? "permanent" : "temporary"}
          open={isDesktop ? true : mobileOpen}
          onClose={() => setMobileOpen(false)}
          ModalProps={{ keepMounted: true }}
          sx={{
            "& .MuiDrawer-paper": {
              width: DRAWER_WIDTH,
              boxSizing: "border-box",
              borderRight: 1,
              borderColor: "divider",
            },
          }}
        >
          <Toolbar />
          {navList}
        </Drawer>
      </Box>

      <Box component="main" sx={{ flexGrow: 1, p: 3, width: { md: `calc(100% - ${DRAWER_WIDTH}px)` } }}>
        <Toolbar />
        {children}
      </Box>
    </Box>
  );
}
