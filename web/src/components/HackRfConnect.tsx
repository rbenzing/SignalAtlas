import { useState } from "react";
import Button from "@mui/material/Button";
import Box from "@mui/material/Box";
import Chip from "@mui/material/Chip";
import IconButton from "@mui/material/IconButton";
import Popover from "@mui/material/Popover";
import Stack from "@mui/material/Stack";
import TextField from "@mui/material/TextField";
import FormControlLabel from "@mui/material/FormControlLabel";
import Switch from "@mui/material/Switch";
import Tooltip from "@mui/material/Tooltip";
import CircularProgress from "@mui/material/CircularProgress";
import SettingsInputAntennaIcon from "@mui/icons-material/SettingsInputAntenna";
import GraphicEqIcon from "@mui/icons-material/GraphicEq";
import TuneIcon from "@mui/icons-material/Tune";
import { useSdr } from "../sdr/SdrProvider";
import { useLive } from "../live/LiveProvider";
import BandSelector from "./BandSelector";

export default function HackRfConnect() {
  const sdr = useSdr();
  const { spectrumFps } = useLive();
  const [anchor, setAnchor] = useState<HTMLElement | null>(null);

  if (sdr.status === "idle" || sdr.status === "error") {
    return (
      <Tooltip title={sdr.error ?? "Connect a HackRF One over WebUSB"} describeChild>
        <Button
          size="small"
          variant="outlined"
          color={sdr.status === "error" ? "error" : "primary"}
          startIcon={<SettingsInputAntennaIcon />}
          onClick={() => void sdr.connect()}
        >
          Connect HackRF
        </Button>
      </Tooltip>
    );
  }

  if (sdr.status === "requesting") {
    return <Chip icon={<CircularProgress size={14} />} label="Connecting…" size="small" />;
  }

  // ready (adopted, not scanning) or streaming. Both share the device chip, band selector, tuning
  // popover, and Disconnect; streaming additionally shows the live scan chip and a Stop button.
  const streaming = sdr.status === "streaming";
  const scanning = spectrumFps > 0;
  const centerMhz = (sdr.tuning.centerFreqHz / 1e6).toFixed(3);
  return (
    <Box sx={{ display: "flex", alignItems: "center", gap: 1 }}>
      <Chip
        color="success"
        variant="outlined"
        size="small"
        icon={<SettingsInputAntennaIcon />}
        label={`HackRF ${sdr.serial ?? ""}${sdr.drops > 0 ? ` · ${sdr.drops} drops` : ""}`}
      />
      {streaming ? (
        <Tooltip
          title={scanning ? `Receiving live IQ at ${centerMhz} MHz` : "Connected but no IQ frames arriving yet"}
          describeChild
        >
          <Chip
            color={scanning ? "success" : "warning"}
            variant="filled"
            size="small"
            icon={<GraphicEqIcon />}
            label={scanning ? `Scanning · ${spectrumFps} fps · ${centerMhz} MHz` : "Scanning · no data"}
          />
        </Tooltip>
      ) : (
        <Tooltip title="Idle — pick a frequency from the band selector to start scanning" describeChild>
          <Chip
            color="default"
            variant="outlined"
            size="small"
            icon={<GraphicEqIcon />}
            label="Idle · select a frequency"
          />
        </Tooltip>
      )}
      <BandSelector />
      <Tooltip title="Tuning">
        <IconButton size="small" onClick={(e) => setAnchor(e.currentTarget)} aria-label="HackRF tuning">
          <TuneIcon fontSize="small" />
        </IconButton>
      </Tooltip>
      {streaming && (
        <Button size="small" color="warning" variant="outlined" onClick={() => void sdr.stop()}>
          Stop
        </Button>
      )}
      <Button size="small" color="inherit" onClick={() => void sdr.disconnect()}>
        Disconnect
      </Button>

      <Popover
        open={Boolean(anchor)}
        anchorEl={anchor}
        onClose={() => setAnchor(null)}
        anchorOrigin={{ vertical: "bottom", horizontal: "right" }}
      >
        <Stack spacing={2} sx={{ p: 2, width: 240 }}>
          <TextField
            label="Center (MHz)"
            type="number"
            size="small"
            defaultValue={sdr.tuning.centerFreqHz / 1e6}
            onChange={(e) => void sdr.setTuning({ centerFreqHz: Number(e.target.value) * 1e6 })}
            slotProps={{ htmlInput: { min: 1, max: 6000, step: "any" } }}
          />
          <TextField
            label="Sample rate (MS/s)"
            type="number"
            size="small"
            defaultValue={sdr.tuning.sampleRateHz / 1e6}
            onChange={(e) => void sdr.setTuning({ sampleRateHz: Number(e.target.value) * 1e6 })}
            slotProps={{ htmlInput: { min: 2, max: 20, step: "any" } }}
          />
          <TextField
            label="LNA gain (dB)"
            type="number"
            size="small"
            defaultValue={sdr.tuning.lnaGain}
            onChange={(e) => void sdr.setTuning({ lnaGain: Number(e.target.value) })}
          />
          <TextField
            label="VGA gain (dB)"
            type="number"
            size="small"
            defaultValue={sdr.tuning.vgaGain}
            onChange={(e) => void sdr.setTuning({ vgaGain: Number(e.target.value) })}
          />
          <FormControlLabel
            control={
              <Switch
                checked={sdr.tuning.ampEnable}
                onChange={(e) => void sdr.setTuning({ ampEnable: e.target.checked })}
              />
            }
            label="Amp"
          />
          <Tooltip
            title="Supplies +3.3 V to the antenna port — enable ONLY for an external powered LNA; can damage passive antennas / other equipment."
            describeChild
          >
            <FormControlLabel
              control={
                <Switch
                  checked={sdr.tuning.biasTee}
                  onChange={(e) => void sdr.setTuning({ biasTee: e.target.checked })}
                />
              }
              label="Bias-tee"
            />
          </Tooltip>
        </Stack>
      </Popover>
    </Box>
  );
}
