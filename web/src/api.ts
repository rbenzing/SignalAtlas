// Typed client for the Signal Atlas API (SPEC §7.5 envelope, §9.2 endpoints).

import { useCallback, useEffect, useRef, useState } from "react";

export interface ApiEnvelope<T> {
  schemaVersion: string;
  correlationId: string;
  payload: T;
}

export interface EvidenceItem {
  feature: string;
  value: string;
  weight: number;
}

export interface Signal {
  id: number;
  time: string;
  observationId: number;
  emitterId: string | null;
  deviceId: string | null;
  protocol: string;
  confidence: number;
  classifier: string;
  evidence: EvidenceItem[];
  centerFreqHz: number;
  bandwidthHz: number;
  durationMs: number | null;
  features: Record<string, number>;
}

export interface Device {
  id: string;
  deviceType: string;
  primaryIdentifier: string | null;
  identifiers: Record<string, string>;
  vendor: string | null;
  protocol: string;
  confidence: number;
  evidence: EvidenceItem[];
  latitude: number | null;
  longitude: number | null;
  altitudeFt: number | null;
}

export interface Emitter {
  id: string;
  deviceId: string | null;
  protocol: string;
  freqCenterHz: number;
  freqStabilityHz: number;
  estLatitude: number | null;
  estLongitude: number | null;
  estUncertaintyM: number | null;
  signalCount: number;
  confidence: number;
  identifiers: Record<string, string>;
  evidence: EvidenceItem[];
}

export interface Alert {
  id: string;
  kind: string;
  severity: string;
  summary: string;
  time: string;
  evidence: EvidenceItem[];
}

export interface Summary {
  signalCount: number;
  deviceCount: number;
  alertCount: number;
}

export interface SpectrumFrame {
  time: string;
  centerFreqHz: number;
  sampleRateHz: number;
  powerDbfs: number[];
}

export interface SpectrumOccupancy {
  centerFreqHz: number;
  sampleRateHz: number;
  freqHz: number[];
  powerDbfs: number[];
  thresholdDbfs: number;
  occupiedFraction: number;
}

/** One priority band's coverage/last-seen indicator (SPEC §4.4). */
export interface SpectrumCoverageBand {
  key: string;
  label: string;
  lowHz: number;
  highHz: number;
  lastSeen: string | null;
  ageSeconds: number | null;
  covered: boolean;
}

const BASE = import.meta.env.VITE_API_BASE ?? "";

/** Fetch an enveloped endpoint and return the unwrapped payload. */
export async function getEnvelope<T>(path: string): Promise<T> {
  const resp = await fetch(`${BASE}/api/v1${path}`);
  if (!resp.ok) throw new Error(`GET ${path} failed: ${resp.status}`);
  const env = (await resp.json()) as ApiEnvelope<T>;
  return env.payload;
}

export const getSignals = () => getEnvelope<Signal[]>("/signals");
export const getDevices = () => getEnvelope<Device[]>("/devices");
export const getAlerts = () => getEnvelope<Alert[]>("/alerts");
export const getSummary = () => getEnvelope<Summary>("/summary");
export const getEmitters = () => getEnvelope<Emitter[]>("/emitters");
export const getEmitter = (id: string) =>
  getEnvelope<Emitter>(`/emitters/${encodeURIComponent(id)}`);
export const getSpectrumFrames = () => getEnvelope<SpectrumFrame[]>("/spectrum/frames");
export const getSpectrumOccupancy = () => getEnvelope<SpectrumOccupancy>("/spectrum/occupancy");
export const getSpectrumCoverage = () => getEnvelope<SpectrumCoverageBand[]>("/spectrum/coverage");

/** Ungated health probe — not enveloped. Returns true when reachable + ok. */
export async function getHealth(): Promise<boolean> {
  try {
    const resp = await fetch(`${BASE}/health`);
    return resp.ok;
  } catch {
    return false;
  }
}

export interface PollingState<T> {
  data: T | null;
  loading: boolean;
  error: string | null;
}

/**
 * Poll `fn` every `intervalMs`. Fetches immediately, then on the interval.
 * Cleans up the timer and ignores late results after unmount.
 */
export function usePolling<T>(fn: () => Promise<T>, intervalMs: number): PollingState<T> {
  const [state, setState] = useState<PollingState<T>>({
    data: null,
    loading: true,
    error: null,
  });
  // Keep the latest fn without retriggering the interval each render.
  const fnRef = useRef(fn);
  fnRef.current = fn;

  const run = useCallback(async (alive: () => boolean) => {
    try {
      const data = await fnRef.current();
      if (alive()) setState({ data, loading: false, error: null });
    } catch (e) {
      if (alive()) setState((s) => ({ ...s, loading: false, error: String(e) }));
    }
  }, []);

  useEffect(() => {
    let mounted = true;
    const alive = () => mounted;
    void run(alive);
    const timer = window.setInterval(() => void run(alive), intervalMs);
    return () => {
      mounted = false;
      window.clearInterval(timer);
    };
  }, [run, intervalMs]);

  return state;
}
