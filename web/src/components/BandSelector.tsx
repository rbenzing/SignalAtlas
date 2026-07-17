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
// beneath a band-center header. Every item retunes via the existing setTuning — the
// active label/checkmark derive purely from the current tuning center (no extra state).
export default function BandSelector() {
  const sdr = useSdr();
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);
  const center = sdr.tuning.centerFreqHz;
  const current = activeBand(center);

  const tune = (centerFreqHz: number, sampleRateHz: number) => {
    void sdr.setTuning({ centerFreqHz, sampleRateHz });
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
      >
        {current?.label ?? "Custom"}
      </Button>
      <Menu anchorEl={anchor} open={Boolean(anchor)} onClose={() => setAnchor(null)}>
        {bandPresets.flatMap((band) => [
          <MenuItem
            key={band.key}
            onClick={() => tune(band.centerFreqHz, band.sampleRateHz)}
            sx={{ fontWeight: 600 }}
          >
            <ListItemIcon>{center === band.centerFreqHz ? <CheckIcon fontSize="small" /> : null}</ListItemIcon>
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
