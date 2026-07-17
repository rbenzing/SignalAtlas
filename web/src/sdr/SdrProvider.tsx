import { createContext, useCallback, useContext, useReducer, useRef } from "react";
import { HackRfDevice, type HackRfDeviceInfo } from "./hackrf";
import { IqSocket, type IqStreamConfig } from "./iqSocket";

export type SdrStatus = "idle" | "requesting" | "streaming" | "error";

export interface SdrTuning {
  centerFreqHz: number;
  sampleRateHz: number;
  lnaGain: number;
  vgaGain: number;
  ampEnable: boolean;
}

export const DEFAULT_TUNING: SdrTuning = {
  centerFreqHz: 915_000_000,
  sampleRateHz: 2_000_000,
  lnaGain: 16,
  vgaGain: 20,
  ampEnable: false,
};

export interface SdrState {
  status: SdrStatus;
  serial: string | null;
  firmware: string | null;
  error: string | null;
  tuning: SdrTuning;
  drops: number;
}

export const INITIAL_SDR_STATE: SdrState = {
  status: "idle",
  serial: null,
  firmware: null,
  error: null,
  tuning: DEFAULT_TUNING,
  drops: 0,
};

export type SdrAction =
  | { type: "requesting" }
  | { type: "connected"; info: HackRfDeviceInfo }
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
        status: "streaming",
        serial: action.info.serialNumber,
        firmware: action.info.firmwareVersion,
        error: null,
      };
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
  setTuning: (patch: Partial<SdrTuning>) => Promise<void>;
};

const SdrContext = createContext<SdrContextValue | null>(null);

function iqSocketUrl(): string {
  const proto = window.location.protocol === "https:" ? "wss" : "ws";
  return `${proto}://${window.location.host}/ingest/iq`;
}

const SAMPLES_PER_BLOCK = 8192;

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
  const configFrame = (t: SdrTuning): IqStreamConfig => ({
    type: "config",
    centerFreqHz: t.centerFreqHz,
    sampleRateHz: t.sampleRateHz,
    samplesPerBlock: SAMPLES_PER_BLOCK,
    collectorId: "web-hackrf-1",
  });

  const applyTuningToDevice = async (dev: HackRfDevice, t: SdrTuning) => {
    await dev.setSampleRate(t.sampleRateHz);
    await dev.setBasebandFilter(t.sampleRateHz);
    await dev.setFrequency(t.centerFreqHz);
    await dev.setLnaGain(t.lnaGain);
    await dev.setVgaGain(t.vgaGain);
    await dev.setAmpEnable(t.ampEnable);
  };

  // Teardown closes/releases the device and socket and nulls the refs, but does NOT
  // dispatch and does NOT reset tuningRef — it must leave `state.tuning`/`tuningRef`
  // untouched so an error path doesn't silently lose the user's tuning selection.
  const teardown = useCallback(async () => {
    try { await deviceRef.current?.disconnect(); } catch { /* ignore */ }
    try { wsRef.current?.close(); } catch { /* ignore */ }
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

  const startStreaming = async (info: HackRfDeviceInfo, dev: HackRfDevice) => {
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
    iq.sendConfig(configFrame(t));

    await dev.startRx(
      (samples) => {
        iq.sendIq(samples);
        if (iq.drops !== dropsRef.current) {
          dropsRef.current = iq.drops;
          dispatch({ type: "drops", drops: iq.drops });
        }
      },
      (err) => {
        if (err) dispatch({ type: "error", message: err.message });
      },
    );
    dispatch({ type: "connected", info });
  };

  const connect = useCallback(async () => {
    dispatch({ type: "requesting" });
    try {
      const dev = new HackRfDevice();
      deviceRef.current = dev;
      const info = await dev.connect();
      await startStreaming(info, dev);
    } catch (e) {
      await teardown();
      dispatch({ type: "error", message: (e as Error).message });
    }
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [teardown]);

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

  const value: SdrContextValue = { ...state, connect, disconnect, setTuning };
  return <SdrContext.Provider value={value}>{children}</SdrContext.Provider>;
}

export function useSdr(): SdrContextValue {
  const ctx = useContext(SdrContext);
  if (!ctx) throw new Error("useSdr must be used within a SdrProvider");
  return ctx;
}
