import { createContext, useCallback, useContext, useEffect, useReducer, useRef } from "react";
import { HackRfDevice, type HackRfDeviceInfo, computeBasebandFilterBw } from "./hackrf";
import { IqSocket, type IqStreamConfig } from "./iqSocket";

// "ready" = a HackRF is adopted and open but NOT streaming (transceiver OFF, no IQ socket, no
// frequency selected). Connecting resolves here — the radio does not scan until a frequency is
// chosen from the band selector. "streaming" is the only state that actually captures IQ.
export type SdrStatus = "idle" | "requesting" | "ready" | "streaming" | "error";

export interface SdrTuning {
  centerFreqHz: number;
  sampleRateHz: number;
  lnaGain: number;
  vgaGain: number;
  ampEnable: boolean;
  /** Antenna-port bias-tee (+3.3 V, RX-side only — powers an external LNA). Default off. */
  biasTee: boolean;
}

export const DEFAULT_TUNING: SdrTuning = {
  centerFreqHz: 915_000_000,
  sampleRateHz: 2_000_000,
  lnaGain: 16,
  vgaGain: 20,
  ampEnable: false,
  biasTee: false,
};

export interface SdrState {
  status: SdrStatus;
  serial: string | null;
  firmware: string | null;
  error: string | null;
  tuning: SdrTuning;
  /** Center frequency currently being captured, or null when not streaming (idle/ready/error).
   * Drives the band selector's active-selection/checkmark — null → nothing selected. */
  activeFreqHz: number | null;
  drops: number;
}

export const INITIAL_SDR_STATE: SdrState = {
  status: "idle",
  serial: null,
  firmware: null,
  error: null,
  tuning: DEFAULT_TUNING,
  activeFreqHz: null,
  drops: 0,
};

export type SdrAction =
  | { type: "requesting" }
  // "connected" = adopted & ready (NOT streaming): a frequency must still be selected to scan.
  | { type: "connected"; info: HackRfDeviceInfo }
  | { type: "streaming"; centerFreqHz: number }
  // "stopped" = Stop pressed: streaming halted, device stays adopted → back to ready.
  | { type: "stopped" }
  | { type: "error"; message: string }
  | { type: "disconnected" }
  | { type: "tuning"; patch: Partial<SdrTuning> }
  | { type: "drops"; drops: number };

export function sdrReducer(state: SdrState, action: SdrAction): SdrState {
  switch (action.type) {
    case "requesting":
      return { ...state, status: "requesting", error: null };
    case "connected":
      return {
        ...state,
        status: "ready",
        serial: action.info.serialNumber,
        firmware: action.info.firmwareVersion,
        error: null,
        activeFreqHz: null,
      };
    case "streaming":
      return { ...state, status: "streaming", error: null, activeFreqHz: action.centerFreqHz };
    case "stopped":
      // Keep serial/firmware/tuning; only drop the active capture. Device remains adopted.
      return { ...state, status: "ready", activeFreqHz: null };
    case "error":
      return { ...state, status: "error", error: action.message };
    case "disconnected":
      return { ...INITIAL_SDR_STATE };
    case "tuning":
      return { ...state, tuning: { ...state.tuning, ...action.patch } };
    case "drops":
      return { ...state, drops: action.drops };
    default:
      return state;
  }
}

type SdrContextValue = SdrState & {
  connect: () => Promise<void>;
  disconnect: () => Promise<void>;
  /** Start capturing at the given frequency (from "ready"), or retune an active stream in place. */
  tune: (centerFreqHz: number, sampleRateHz: number) => Promise<void>;
  /** Stop capturing but keep the device adopted (→ "ready"); clears the selected frequency. */
  stop: () => Promise<void>;
  setTuning: (patch: Partial<SdrTuning>) => Promise<void>;
};

const SdrContext = createContext<SdrContextValue | null>(null);

function iqSocketUrl(): string {
  const proto = window.location.protocol === "https:" ? "wss" : "ws";
  return `${proto}://${window.location.host}/ingest/iq`;
}

const SAMPLES_PER_BLOCK = 8192;

/** The config frame plus the RX-config provenance fields (SPEC §8.1): gain stages, baseband
 * filter width, and bias-tee, mirrored from what applyTuningToDevice sets on the HackRF. Field
 * names MUST match IqIngressEndpoint's IqConfig JsonPropertyName casing exactly (landmine #1). */
type IqConfigFrame = IqStreamConfig & {
  lnaDb: number;
  vgaDb: number;
  ampEnable: boolean;
  basebandBwHz: number;
  biasTee: boolean;
};

export function SdrProvider({ children }: { children: React.ReactNode }) {
  const [state, dispatch] = useReducer(sdrReducer, INITIAL_SDR_STATE);
  const deviceRef = useRef<HackRfDevice | null>(null);
  const wsRef = useRef<WebSocket | null>(null);
  const iqRef = useRef<IqSocket | null>(null);
  const tuningRef = useRef<SdrTuning>(DEFAULT_TUNING);
  const dropsRef = useRef(0);

  // configFrame/applyTuningToDevice/startStreaming MUST read tuning from tuningRef,
  // never from `state`, to avoid stale-closure bugs (refs are updated synchronously
  // by setTuning; `state.tuning` only updates on the next render).
  // Baseband filter width is the ACTUAL width the device applies: applyTuningToDevice calls
  // setBasebandFilter(t.sampleRateHz), which rounds down to a valid HackRF width via
  // computeBasebandFilterBw (e.g. 2 MHz -> 1.75 MHz). Report that rounded value so the provenance
  // reflects the real analog passband, not the raw requested rate (SPEC §8.1 honest provenance).
  const configFrame = (t: SdrTuning): IqConfigFrame => ({
    type: "config",
    centerFreqHz: t.centerFreqHz,
    sampleRateHz: t.sampleRateHz,
    samplesPerBlock: SAMPLES_PER_BLOCK,
    collectorId: "web-hackrf-1",
    lnaDb: t.lnaGain,
    vgaDb: t.vgaGain,
    ampEnable: t.ampEnable,
    basebandBwHz: computeBasebandFilterBw(t.sampleRateHz),
    biasTee: t.biasTee,
  });

  const applyTuningToDevice = async (dev: HackRfDevice, t: SdrTuning) => {
    await dev.setSampleRate(t.sampleRateHz);
    await dev.setBasebandFilter(t.sampleRateHz);
    await dev.setFrequency(t.centerFreqHz);
    await dev.setLnaGain(t.lnaGain);
    await dev.setVgaGain(t.vgaGain);
    await dev.setAmpEnable(t.ampEnable);
    await dev.setAntennaEnable(t.biasTee);
  };

  // Teardown closes/releases the device and socket and nulls the refs, but does NOT
  // dispatch and does NOT reset tuningRef — it must leave `state.tuning`/`tuningRef`
  // untouched so an error path doesn't silently lose the user's tuning selection.
  //
  // Detach the socket's handlers BEFORE closing it: an intentional teardown (disconnect(),
  // or the mid-stream-error path below) must close the socket silently. Only a close/error
  // event that fires while the handlers are still attached (i.e. NOT triggered by our own
  // teardown) is "unexpected" and should surface an error — see startStreaming.
  const teardown = useCallback(async () => {
    try { await deviceRef.current?.disconnect(); } catch { /* ignore */ }
    const ws = wsRef.current;
    if (ws) {
      ws.onopen = null;
      ws.onclose = null;
      ws.onerror = null;
      try { ws.close(); } catch { /* ignore */ }
    }
    deviceRef.current = null;
    wsRef.current = null;
    iqRef.current = null;
  }, []);

  // `disconnect` is defined before `connect` so `connect`'s catch block can call
  // `teardown` without triggering a TS/eslint "used before declaration" error.
  const disconnect = useCallback(async () => {
    await teardown();
    tuningRef.current = DEFAULT_TUNING;
    dispatch({ type: "disconnected" });
  }, [teardown]);

  // Soft stop: halt RX and close the IQ socket but KEEP the device adopted/open (dev.stop() sets
  // the transceiver OFF without releasing the USB interface), so a subsequent tune() restarts
  // without re-prompting the chooser. Detaches the socket handlers before close() so the
  // unexpected-close path never fires for this intentional stop. Leaves deviceRef + tuningRef intact.
  const stopStreaming = useCallback(async () => {
    try { await deviceRef.current?.stop(); } catch { /* ignore */ }
    const ws = wsRef.current;
    if (ws) {
      ws.onopen = null;
      ws.onclose = null;
      ws.onerror = null;
      try { ws.close(); } catch { /* ignore */ }
    }
    wsRef.current = null;
    iqRef.current = null;
  }, []);

  const startStreaming = async (dev: HackRfDevice) => {
    const t = tuningRef.current;
    await applyTuningToDevice(dev, t);

    const ws = new WebSocket(iqSocketUrl());
    ws.binaryType = "arraybuffer";
    wsRef.current = ws;
    const iq = new IqSocket(ws);
    iqRef.current = iq;

    await new Promise<void>((resolve, reject) => {
      const timer = setTimeout(() => reject(new Error("IQ WebSocket connection timed out.")), 8000);
      ws.onopen = () => { clearTimeout(timer); resolve(); };
      ws.onerror = () => { clearTimeout(timer); reject(new Error("IQ WebSocket failed to open.")); };
    });

    // Persistent post-open handlers: a backend restart or network drop closes the socket
    // without ever calling our own teardown(), which would otherwise leave the UI stuck at
    // "streaming" with a dead spectrum (ws.send() on a closing/closed socket is a silent
    // no-op, so nothing else would ever surface this). teardown() detaches these handlers
    // before it closes the socket itself, so an INTENTIONAL disconnect()/mid-stream-error
    // teardown never reaches this branch — only a genuinely unexpected close does.
    // The `handled` flag guards against a double dispatch if both onerror and onclose fire
    // for the same underlying failure (a common browser pattern for abnormal closures).
    let handled = false;
    const handleUnexpectedClose = () => {
      if (handled) return;
      handled = true;
      void teardown();
      dispatch({ type: "error", message: "IQ stream disconnected." });
    };
    ws.onclose = handleUnexpectedClose;
    ws.onerror = handleUnexpectedClose;

    iq.sendConfig(configFrame(t));

    await dev.startRx(
      (samples) => {
        iq.sendIq(samples);
        if (iq.drops !== dropsRef.current) {
          dropsRef.current = iq.drops;
          dispatch({ type: "drops", drops: iq.drops });
        }
      },
      async (err) => {
        // A mid-stream device error (readLoop failure) must release the claimed USB
        // interface + socket before surfacing the error, otherwise a later connect()
        // overwrites deviceRef without releasing the old interface and reconnect fails
        // until page reload. teardown() preserves tuningRef, so the user's tuning
        // selection survives the error.
        await teardown();
        dispatch({ type: "error", message: err?.message ?? "HackRF stream ended." });
      },
    );
    dispatch({ type: "streaming", centerFreqHz: t.centerFreqHz });
  };

  // One-click silent reconnect (Spec §4.3/§9): if the browser already granted USB access to
  // this HackRF in a previous session, navigator.usb.getDevices() returns it WITHOUT ever
  // prompting the chooser, and HackRfDevice.adopt() opens it directly (vs. connect(), which
  // calls requestDevice() and always prompts). Runs once on mount.
  useEffect(() => {
    let cancelled = false;
    (async () => {
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      const usb = (navigator as Navigator & { usb?: { getDevices(): Promise<any[]> } }).usb;
      if (!usb?.getDevices) return; // WebUSB unsupported / not available in this browser
      // eslint-disable-next-line @typescript-eslint/no-explicit-any
      let devices: any[];
      try {
        devices = await usb.getDevices();
      } catch {
        return;
      }
      // Only adopt a previously-authorized HackRF (vendor 0x1d50, product 0x6089/0x604b).
      const hackrf = devices.find(
        (d) => d?.vendorId === 0x1d50 && (d?.productId === 0x6089 || d?.productId === 0x604b),
      );
      if (cancelled || !hackrf) return;
      dispatch({ type: "requesting" });
      try {
        const dev = new HackRfDevice();
        deviceRef.current = dev;
        const info = await dev.adopt(hackrf);
        if (cancelled) {
          await teardown();
          return;
        }
        // Adopt only — do NOT auto-scan. The operator selects a frequency to begin capture.
        dispatch({ type: "connected", info });
      } catch (e) {
        if (!cancelled) {
          await teardown();
          dispatch({ type: "error", message: (e as Error).message });
        }
      }
    })();
    return () => {
      cancelled = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const connect = useCallback(async () => {
    dispatch({ type: "requesting" });
    try {
      const dev = new HackRfDevice();
      deviceRef.current = dev;
      const info = await dev.connect();
      // Adopt only — do NOT auto-scan. The operator selects a frequency to begin capture.
      dispatch({ type: "connected", info });
    } catch (e) {
      await teardown();
      dispatch({ type: "error", message: (e as Error).message });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [teardown]);

  // Begin capture at a frequency (from "ready"), or retune an active stream in place. This is the
  // ONLY way to start scanning — connecting alone never streams.
  const tune = useCallback(async (centerFreqHz: number, sampleRateHz: number) => {
    const next = { ...tuningRef.current, centerFreqHz, sampleRateHz };
    tuningRef.current = next;
    dispatch({ type: "tuning", patch: { centerFreqHz, sampleRateHz } });
    const dev = deviceRef.current;
    if (!dev) return;
    try {
      if (iqRef.current) {
        // Already streaming → retune the live device + resend provenance; move the active marker.
        await applyTuningToDevice(dev, next);
        iqRef.current.sendConfig(configFrame(next));
        dispatch({ type: "streaming", centerFreqHz });
      } else {
        // Ready → open the socket and start RX (startStreaming dispatches "streaming").
        await startStreaming(dev);
      }
    } catch (e) {
      await stopStreaming();
      dispatch({ type: "error", message: (e as Error).message });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [stopStreaming]);

  // Stop scanning but keep the device adopted (→ "ready"); clears the selected frequency.
  const stop = useCallback(async () => {
    await stopStreaming();
    dispatch({ type: "stopped" });
  }, [stopStreaming]);

  const setTuning = useCallback(async (patch: Partial<SdrTuning>) => {
    const next = { ...tuningRef.current, ...patch };
    tuningRef.current = next;
    dispatch({ type: "tuning", patch });
    const dev = deviceRef.current;
    if (dev?.isConnected) {
      await applyTuningToDevice(dev, next);
      iqRef.current?.sendConfig(configFrame(next));
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, []);

  const value: SdrContextValue = { ...state, connect, disconnect, tune, stop, setTuning };
  return <SdrContext.Provider value={value}>{children}</SdrContext.Provider>;
}

export function useSdr(): SdrContextValue {
  const ctx = useContext(SdrContext);
  if (!ctx) throw new Error("useSdr must be used within a SdrProvider");
  return ctx;
}
