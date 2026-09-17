import { useState } from "react";
import Box from "@mui/material/Box";
import Button from "@mui/material/Button";
import Chip from "@mui/material/Chip";
import Divider from "@mui/material/Divider";
import Stack from "@mui/material/Stack";
import TextField from "@mui/material/TextField";
import Typography from "@mui/material/Typography";
import SendIcon from "@mui/icons-material/Send";
import ChartCard from "../components/ChartCard";
import EvidenceList from "../components/EvidenceList";
import { chartTokens } from "../theme/palette";
import { useColorMode } from "../theme/ColorModeContext";
import { postAnalystQuery, type AnalystAnswer } from "../api";

const EXAMPLE_PROMPTS = [
  "What changed today?",
  "What's transmitting on 433 MHz?",
  "Where is the strongest emitter?",
  "Any anomalies?",
];

interface Turn {
  question: string;
  answer?: AnalystAnswer;
  error?: string;
}

export default function Analyst() {
  const { mode } = useColorMode();
  const t = chartTokens[mode];
  const [input, setInput] = useState("");
  const [loading, setLoading] = useState(false);
  const [turns, setTurns] = useState<Turn[]>([]);

  const canSend = input.trim().length > 0 && !loading;

  async function submit() {
    const text = input.trim();
    if (!text || loading) return;
    setLoading(true);
    setInput("");
    try {
      const answer = await postAnalystQuery(text);
      setTurns((prev) => [{ question: text, answer }, ...prev]);
    } catch (e) {
      setTurns((prev) => [{ question: text, error: String(e) }, ...prev]);
    } finally {
      setLoading(false);
    }
  }

  return (
    <ChartCard title="Spectrum Analyst">
      <Stack spacing={2}>
        <Stack direction="row" spacing={1} sx={{ flexWrap: "wrap" }}>
          {EXAMPLE_PROMPTS.map((p) => (
            <Chip
              key={p}
              label={p}
              size="small"
              variant="outlined"
              onClick={() => setInput(p)}
              sx={{ cursor: "pointer" }}
            />
          ))}
        </Stack>

        <Stack direction="row" spacing={1}>
          <TextField
            fullWidth
            size="small"
            // a11y: a placeholder is NOT a label — it disappears on first keystroke and screen-reader
            // support for using it as the accessible name is inconsistent (WCAG 3.3.2 / 4.1.2).
            // Give the field a real name and keep the placeholder as the worked example.
            label="Ask the spectrum"
            aria-label="Ask the spectrum a question about signals, devices, emitters, or alerts"
            placeholder="Ask about signals, devices, emitters, or alerts…"
            value={input}
            onChange={(e) => setInput(e.target.value)}
            onKeyDown={(e) => {
              if (e.key === "Enter") {
                e.preventDefault();
                void submit();
              }
            }}
          />
          <Button
            variant="contained"
            endIcon={<SendIcon fontSize="small" />}
            disabled={!canSend}
            onClick={() => void submit()}
          >
            Send
          </Button>
        </Stack>

        {turns.length === 0 && (
          <Typography variant="body2" color="text.secondary">
            Ask a question above to get a grounded, cited answer.
          </Typography>
        )}

        {/*
          a11y: answers arrive asynchronously, so without a live region a screen-reader user presses
          Send and hears nothing at all — they would have to hunt for the result manually. `polite`
          waits for a pause rather than interrupting; `aria-busy` covers the in-flight gap so the
          pending state is perceivable too.
        */}
        <Stack spacing={1.5} role="status" aria-live="polite" aria-busy={loading}>
          {turns.map((turn, i) => (
            <Box
              key={i}
              sx={{
                border: 1,
                borderColor: "divider",
                borderRadius: 1,
                p: 1.5,
                bgcolor: t.surfaceAlt,
              }}
            >
              <Typography variant="subtitle2">{turn.question}</Typography>
              <Divider sx={{ my: 1 }} />
              {turn.error && (
                <Typography variant="body2" color="error">
                  Request failed: {turn.error}
                </Typography>
              )}
              {turn.answer && (
                <>
                  <Stack direction="row" spacing={1} sx={{ alignItems: "center", mb: 1 }}>
                    <Chip
                      size="small"
                      label={turn.answer.mode === "cloud" ? "Cloud" : "Offline"}
                      color={turn.answer.mode === "cloud" ? "secondary" : "default"}
                    />
                    <Chip size="small" label={turn.answer.queryType} variant="outlined" />
                  </Stack>
                  <Typography
                    variant="body2"
                    color={turn.answer.text === "None found." ? "text.secondary" : "text.primary"}
                    sx={{ mb: 1 }}
                  >
                    {turn.answer.text}
                  </Typography>
                  <Typography variant="caption" color="text.secondary">
                    Citations
                  </Typography>
                  {turn.answer.citations.length === 0 ? (
                    <Typography variant="caption" color="text.secondary" sx={{ display: "block" }}>
                      No records cited.
                    </Typography>
                  ) : (
                    <EvidenceList items={turn.answer.citations} />
                  )}
                </>
              )}
            </Box>
          ))}
        </Stack>
      </Stack>
    </ChartCard>
  );
}
