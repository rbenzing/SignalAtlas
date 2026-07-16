// Live data context: seeds from REST, then merges hub push events on top.
// Polling is retained as the fallback — when the hub is not connected the
// polled snapshots drive the state so the app keeps updating regardless.

import { createContext, useContext, useEffect, useRef, useState } from "react";
import {
  getAlerts,
  getEmitters,
  getSignals,
  getSpectrumFrames,
  usePolling,
  type Alert,
  type Emitter,
  type Signal,
  type SpectrumFrame,
} from "../api";
import { useLiveEvent, useLiveHub, type HubStatus } from "./useLiveHub";

const SIGNAL_CAP = 500;
const ALERT_CAP = 500;
const FRAME_CAP = 256;

// Polling cadence used only as the fallback when the hub is down.
const POLL_MS = 5000;
const FRAME_POLL_MS = 1500;

export interface LiveData {
  status: HubStatus;
  signals: Signal[];
  emitters: Emitter[];
  alerts: Alert[];
  /** Rolling waterfall buffer, newest-first, capped at FRAME_CAP. */
  spectrumFrames: SpectrumFrame[];
}

const LiveContext = createContext<LiveData | null>(null);

export function LiveProvider({ children }: { children: React.ReactNode }) {
  const { status } = useLiveHub();

  const [signals, setSignals] = useState<Signal[]>([]);
  const [emitters, setEmitters] = useState<Emitter[]>([]);
  const [alerts, setAlerts] = useState<Alert[]>([]);
  const [spectrumFrames, setSpectrumFrames] = useState<SpectrumFrame[]>([]);

  // Polling drives the seed (first fetch) and the disconnected-fallback refresh.
  const polledSignals = usePolling(getSignals, POLL_MS);
  const polledEmitters = usePolling(getEmitters, POLL_MS);
  const polledAlerts = usePolling(getAlerts, POLL_MS);
  const polledFrames = usePolling(getSpectrumFrames, FRAME_POLL_MS);

  // Latest status without re-triggering the poll-merge effects on every change.
  const statusRef = useRef(status);
  statusRef.current = status;

  const seeded = useRef({ signals: false, emitters: false, alerts: false, frames: false });

  // Apply a polled snapshot when we have not seeded yet, or when the hub is not
  // connected (polling fallback). While connected, live push owns the state.
  useEffect(() => {
    if (!polledSignals.data) return;
    if (!seeded.current.signals || statusRef.current !== "connected") {
      seeded.current.signals = true;
      setSignals(polledSignals.data.slice(0, SIGNAL_CAP));
    }
  }, [polledSignals.data]);

  useEffect(() => {
    if (!polledEmitters.data) return;
    if (!seeded.current.emitters || statusRef.current !== "connected") {
      seeded.current.emitters = true;
      setEmitters(polledEmitters.data);
    }
  }, [polledEmitters.data]);

  useEffect(() => {
    if (!polledAlerts.data) return;
    if (!seeded.current.alerts || statusRef.current !== "connected") {
      seeded.current.alerts = true;
      setAlerts(polledAlerts.data.slice(0, ALERT_CAP));
    }
  }, [polledAlerts.data]);

  useEffect(() => {
    if (!polledFrames.data) return;
    if (!seeded.current.frames || statusRef.current !== "connected") {
      seeded.current.frames = true;
      // Newest-first, capped — matches the live push ordering.
      const newestFirst = [...polledFrames.data].sort((a, b) => b.time.localeCompare(a.time));
      setSpectrumFrames(newestFirst.slice(0, FRAME_CAP));
    }
  }, [polledFrames.data]);

  // Live merges: prepend/upsert/push on top of the seeded state.
  useLiveEvent("signalCreated", (s) => {
    setSignals((prev) => [s, ...prev].slice(0, SIGNAL_CAP));
  });

  useLiveEvent("alertRaised", (a) => {
    setAlerts((prev) => [a, ...prev].slice(0, ALERT_CAP));
  });

  useLiveEvent("emitterUpdated", (e) => {
    setEmitters((prev) => {
      const idx = prev.findIndex((x) => x.id === e.id);
      if (idx === -1) return [e, ...prev];
      const next = prev.slice();
      next[idx] = e;
      return next;
    });
  });

  useLiveEvent("spectrumFrame", (f) => {
    setSpectrumFrames((prev) => [f, ...prev].slice(0, FRAME_CAP));
  });

  const value: LiveData = { status, signals, emitters, alerts, spectrumFrames };
  return <LiveContext.Provider value={value}>{children}</LiveContext.Provider>;
}

/** Access live-merged data + hub connection state. */
export function useLive(): LiveData {
  const ctx = useContext(LiveContext);
  if (!ctx) throw new Error("useLive must be used within a LiveProvider");
  return ctx;
}
