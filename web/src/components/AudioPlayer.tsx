import { useEffect, useRef, useState } from "react";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import LinearProgress from "@mui/material/LinearProgress";
import Slider from "@mui/material/Slider";
import Stack from "@mui/material/Stack";
import ToggleButton from "@mui/material/ToggleButton";
import ToggleButtonGroup from "@mui/material/ToggleButtonGroup";
import Typography from "@mui/material/Typography";
import VolumeUpIcon from "@mui/icons-material/VolumeUp";
import PlayArrowIcon from "@mui/icons-material/PlayArrow";
import StopIcon from "@mui/icons-material/Stop";
import ChartCard from "./ChartCard";
import { AudioStreamPlayer, type AudioMode } from "../audio/AudioStreamPlayer";
import { useSdr } from "../sdr/SdrProvider";

const MODES: { value: AudioMode; label: string }[] = [
  { value: "wbfm", label: "WBFM" },
  { value: "nbfm", label: "NBFM" },
  { value: "am", label: "AM" },
  { value: "usb", label: "USB" },
  { value: "lsb", label: "LSB" },
  { value: "cw", label: "CW" },
];

/** RF Audio Player card (WS-D3, design doc §4): lets the operator LISTEN to the currently-tuned
 * frequency, demodulated server-side. Replaces the Coverage card on the Live Spectrum page. */
export default function AudioPlayer() {
  const { activeFreqHz } = useSdr();
  const [mode, setMode] = useState<AudioMode>("wbfm");
  const [playing, setPlaying] = useState(false);
  const [volume, setVolume] = useState(1);
  const [level, setLevel] = useState(0);
  const playerRef = useRef<AudioStreamPlayer | null>(null);

  const disabled = activeFreqHz === null;

  // Stopping the stream (or the frequency going away) must also drop the meter back to 0 —
  // otherwise it freezes at its last reading.
  useEffect(() => {
    if (disabled && playing) {
      playerRef.current?.stop();
      setPlaying(false);
      setLevel(0);
    }
  }, [disabled, playing]);

  // Tear down any open stream + AudioContext on unmount.
  useEffect(() => {
    return () => {
      playerRef.current?.stop();
      playerRef.current = null;
    };
  }, []);

  const ensurePlayer = (): AudioStreamPlayer => {
    if (!playerRef.current) {
      playerRef.current = new AudioStreamPlayer({ onLevel: setLevel });
    }
    return playerRef.current;
  };

  const handlePlay = () => {
    ensurePlayer().play(mode);
    setPlaying(true);
  };

  const handleStop = () => {
    playerRef.current?.stop();
    setPlaying(false);
    setLevel(0);
  };

  const handleModeChange = (_e: React.MouseEvent<HTMLElement>, next: AudioMode | null) => {
    if (!next) return;
    setMode(next);
    if (playing) {
      playerRef.current?.setMode(next);
    }
  };

  const freqLabel = activeFreqHz === null ? null : `${(activeFreqHz / 1e6).toFixed(3)} MHz`;

  return (
    <ChartCard title="Audio" legend={freqLabel && <Typography variant="body2">{freqLabel}</Typography>}>
      {disabled ? (
        <Typography variant="body2" color="text.secondary">
          Tune a frequency to listen.
        </Typography>
      ) : (
        <Stack spacing={2}>
          <ToggleButtonGroup
            size="small"
            exclusive
            value={mode}
            onChange={handleModeChange}
            aria-label="Demodulation mode"
            sx={{ flexWrap: "wrap", gap: 0.5 }}
          >
            {MODES.map((m) => (
              <ToggleButton
                key={m.value}
                value={m.value}
                sx={{ textTransform: "none", px: 1.5, borderRadius: "4px !important", border: "1px solid" }}
              >
                {m.label}
              </ToggleButton>
            ))}
          </ToggleButtonGroup>

          <Box sx={{ display: "flex", alignItems: "center", gap: 1.5 }}>
            {playing ? (
              <Button
                size="small"
                variant="outlined"
                color="warning"
                startIcon={<StopIcon />}
                onClick={handleStop}
              >
                Stop
              </Button>
            ) : (
              <Button
                size="small"
                variant="contained"
                startIcon={<PlayArrowIcon />}
                onClick={handlePlay}
              >
                Play
              </Button>
            )}
          </Box>

          <Box sx={{ display: "flex", alignItems: "center", gap: 1.5 }}>
            <VolumeUpIcon fontSize="small" color="action" />
            <Slider
              size="small"
              aria-label="Volume"
              min={0}
              max={1}
              step={0.01}
              value={volume}
              onChange={(_e, v) => {
                const next = Array.isArray(v) ? v[0] : v;
                setVolume(next);
                playerRef.current?.setVolume(next);
              }}
              sx={{ maxWidth: 160 }}
            />
          </Box>

          <Box>
            <Typography variant="caption" color="text.secondary">
              Level
            </Typography>
            <LinearProgress
              variant="determinate"
              value={Math.min(100, level * 100)}
              aria-label="Audio level"
              sx={{ height: 6, borderRadius: 1 }}
            />
          </Box>
        </Stack>
      )}
    </ChartCard>
  );
}
