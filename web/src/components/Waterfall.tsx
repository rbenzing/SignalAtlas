import { useEffect, useRef } from "react";
import Box from "@mui/material/Box";
import Typography from "@mui/material/Typography";
import { useColorMode } from "../theme/ColorModeContext";
import { chartTokens } from "../theme/palette";
import { powerRampRgb, normalize, frameSpan, formatMhz } from "../lib/spectrum";
import type { SpectrumFrame } from "../api";

export interface WaterfallProps {
  frames: SpectrumFrame[];
  min: number;
  max: number;
  height?: number;
}

const TICKS = 5;

/**
 * Canvas "frequency rainfall": each frame is a row (newest at top), each bin a
 * cell colored through the sequential-blue power ramp (dBFS min..max). Renders
 * bins×rows into an offscreen ImageData then scales it up with smoothing off,
 * so redraws stay cheap on every poll. Backing store is sized to the device
 * pixel ratio for crispness and re-drawn on resize + color-mode change.
 */
export default function Waterfall({ frames, min, max, height = 320 }: WaterfallProps) {
  const { mode } = useColorMode();
  const canvasRef = useRef<HTMLCanvasElement>(null);
  const wrapRef = useRef<HTMLDivElement>(null);

  useEffect(() => {
    const canvas = canvasRef.current;
    const wrap = wrapRef.current;
    if (!canvas || !wrap || frames.length === 0) return;

    const draw = () => {
      const cssW = wrap.clientWidth || 1;
      const dpr = window.devicePixelRatio || 1;
      canvas.width = Math.max(1, Math.round(cssW * dpr));
      canvas.height = Math.max(1, Math.round(height * dpr));
      canvas.style.width = `${cssW}px`;
      canvas.style.height = `${height}px`;

      const ctx = canvas.getContext("2d");
      if (!ctx) return;

      // Newest frame at the top row.
      const ordered = [...frames].sort((a, b) => b.time.localeCompare(a.time));
      const nRows = ordered.length;
      const nBins = ordered[0].powerDbfs.length;
      if (nBins === 0) return;

      const off = document.createElement("canvas");
      off.width = nBins;
      off.height = nRows;
      const octx = off.getContext("2d");
      if (!octx) return;

      const dc = nBins >> 1; // DC-offset spike lives at the center bin
      const img = octx.createImageData(nBins, nRows);
      for (let r = 0; r < nRows; r++) {
        const row = ordered[r].powerDbfs;
        for (let c = 0; c < nBins; c++) {
          let v = row[c] ?? min;
          // De-emphasize the DC center line: paint it from nearby non-DC power instead of the spike.
          if (Math.abs(c - dc) <= 1) {
            const left = row[dc - 2];
            const right = row[dc + 2];
            v = Number.isFinite(left) && Number.isFinite(right) ? (left + right) / 2 : min;
          }
          const [rr, gg, bb] = powerRampRgb(normalize(v, min, max));
          const idx = (r * nBins + c) * 4;
          img.data[idx] = rr;
          img.data[idx + 1] = gg;
          img.data[idx + 2] = bb;
          img.data[idx + 3] = 255;
        }
      }
      octx.putImageData(img, 0, 0);

      ctx.fillStyle = chartTokens[mode].surface;
      ctx.fillRect(0, 0, canvas.width, canvas.height);
      ctx.imageSmoothingEnabled = false;
      ctx.drawImage(off, 0, 0, nBins, nRows, 0, 0, canvas.width, canvas.height);
    };

    draw();
    const ro = new ResizeObserver(draw);
    ro.observe(wrap);
    return () => ro.disconnect();
  }, [frames, min, max, height, mode]);

  const { startHz, stopHz } = frameSpan(frames[0]);
  const ticks = Array.from({ length: TICKS }, (_, i) => {
    const f = startHz + ((stopHz - startHz) * i) / (TICKS - 1);
    return formatMhz(f, 2);
  });

  return (
    <Box ref={wrapRef} sx={{ width: "100%" }}>
      <canvas ref={canvasRef} style={{ display: "block", width: "100%", height }} />
      <Box sx={{ display: "flex", justifyContent: "space-between", mt: 0.5 }}>
        {ticks.map((t, i) => (
          <Typography key={i} variant="caption" color="text.secondary">
            {t}
          </Typography>
        ))}
      </Box>
      <Typography variant="caption" color="text.secondary" sx={{ display: "block", textAlign: "center" }}>
        Frequency (MHz)
      </Typography>
    </Box>
  );
}
