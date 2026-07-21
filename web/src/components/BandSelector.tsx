import { useState } from "react";
import Button from "@mui/material/Button";
import Menu from "@mui/material/Menu";
import MenuItem from "@mui/material/MenuItem";
import ListItemText from "@mui/material/ListItemText";
import ListItemIcon from "@mui/material/ListItemIcon";
import CheckIcon from "@mui/icons-material/Check";
import ArrowDropDownIcon from "@mui/icons-material/ArrowDropDown";
import { useSdr } from "../sdr/SdrProvider";
import { bandPresets, activeBand } from "../sdr/bandPresets";

// Navbar "frequency changer": a dropdown of bands, each with its channels indented
// beneath a band-center header. Selecting an item is what STARTS capture (from "ready") or
// retunes an active stream — via sdr.tune. The active label/checkmark derive from the
// frequency actually being captured (activeFreqHz): null → nothing selected ("Select frequency").
export default function BandSelector() {
  const sdr = useSdr();
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);
  const open = Boolean(anchor);
  const center = sdr.activeFreqHz;
  const current = center === null ? null : activeBand(center);

  const tune = (centerFreqHz: number, sampleRateHz: number) => {
    void sdr.tune(centerFreqHz, sampleRateHz);
    setAnchor(null);
  };

  return (
    <>
      <Button
        size="small"
        variant="outlined"
        color="inherit"
        endIcon={<ArrowDropDownIcon />}
        onClick={(e) => setAnchor(e.currentTarget)}
        aria-haspopup="menu"
        aria-expanded={open}
        aria-controls={open ? "band-selector-menu" : undefined}
      >
        {current?.label ?? (center === null ? "Select frequency" : "Custom")}
      </Button>
      <Menu id="band-selector-menu" anchorEl={anchor} open={open} onClose={() => setAnchor(null)}>
        {bandPresets.flatMap((band) => [
          // The band-header ✓ marks the band-center default only when no enumerated
          // channel shares that center — otherwise the more specific channel row owns
          // the ✓ and the header stays unmarked (no duplicate check).
          <MenuItem
            key={band.key}
            onClick={() => tune(band.centerFreqHz, band.sampleRateHz)}
            sx={{ fontWeight: 600 }}
          >
            <ListItemIcon>
              {center === band.centerFreqHz && !band.channels.some((c) => c.centerFreqHz === center) ? (
                <CheckIcon fontSize="small" />
              ) : null}
            </ListItemIcon>
            <ListItemText>{band.label}</ListItemText>
          </MenuItem>,
          ...band.channels.map((ch) => (
            <MenuItem
              key={`${band.key}/${ch.key}`}
              onClick={() => tune(ch.centerFreqHz, ch.sampleRateHz)}
              sx={{ pl: 4 }}
            >
              <ListItemIcon>{center === ch.centerFreqHz ? <CheckIcon fontSize="small" /> : null}</ListItemIcon>
              <ListItemText>{ch.label}</ListItemText>
            </MenuItem>
          )),
        ])}
      </Menu>
    </>
  );
}
