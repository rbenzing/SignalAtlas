// Shared SignalR client for the live hub (`/hub/live`). One process-wide
// connection with automatic reconnect; consumers subscribe to typed events and
// to the connection state. Start failures are swallowed so the UI stays in
// polling mode instead of throwing.

import { useEffect, useRef, useState } from "react";
import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from "@microsoft/signalr";
import type { Alert, Device, Emitter, Signal, SpectrumFrame } from "../api";

/** Connection state surfaced to the UI. */
export type HubStatus = "connected" | "reconnecting" | "disconnected";

/** Hub client methods → payload type. Names/DTOs mirror the REST payloads. */
export interface LiveEventMap {
  signalCreated: Signal;
  emitterUpdated: Emitter;
  alertRaised: Alert;
  spectrumFrame: SpectrumFrame;
  deviceDetermined: Device;
}

type EventName = keyof LiveEventMap;
type Handler<K extends EventName> = (payload: LiveEventMap[K]) => void;

const HUB_URL = "/hub/live";
const RETRY_MS = 5000;

const EVENT_NAMES: EventName[] = [
  "signalCreated",
  "emitterUpdated",
  "alertRaised",
  "spectrumFrame",
  "deviceDetermined",
];

const subscribers: { [K in EventName]: Set<Handler<K>> } = {
  signalCreated: new Set(),
  emitterUpdated: new Set(),
  alertRaised: new Set(),
  spectrumFrame: new Set(),
  deviceDetermined: new Set(),
};
const statusSubscribers = new Set<(s: HubStatus) => void>();

let connection: HubConnection | null = null;
let starting = false;
let status: HubStatus = "disconnected";

function setStatus(next: HubStatus): void {
  if (next === status) return;
  status = next;
  for (const cb of statusSubscribers) cb(next);
}

function ensureConnection(): HubConnection {
  if (connection) return connection;
  const conn = new HubConnectionBuilder()
    .withUrl(HUB_URL)
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();

  for (const name of EVENT_NAMES) {
    conn.on(name, (payload: unknown) => {
      const set = subscribers[name] as Set<(p: unknown) => void>;
      for (const cb of set) cb(payload);
    });
  }

  conn.onreconnecting(() => setStatus("reconnecting"));
  conn.onreconnected(() => setStatus("connected"));
  conn.onclose(() => {
    setStatus("disconnected");
    // Automatic reconnect gave up — keep trying so we recover once the hub is back.
    window.setTimeout(start, RETRY_MS);
  });

  connection = conn;
  return conn;
}

/** Idempotently (re)start the shared connection; never rejects. */
function start(): void {
  if (starting) return;
  const conn = ensureConnection();
  if (conn.state !== HubConnectionState.Disconnected) return;
  starting = true;
  conn
    .start()
    .then(() => {
      starting = false;
      setStatus("connected");
    })
    .catch(() => {
      // Stay in polling mode — never surface to the UI. Retry in the background.
      starting = false;
      setStatus("disconnected");
      window.setTimeout(start, RETRY_MS);
    });
}

/** Ensure the shared connection has been kicked off (safe to call repeatedly). */
export function ensureLiveHubStarted(): void {
  start();
}

/** Subscribe to a hub event. Returns an unsubscribe function. */
export function subscribeLive<K extends EventName>(event: K, handler: Handler<K>): () => void {
  const set = subscribers[event] as Set<Handler<K>>;
  set.add(handler);
  return () => {
    set.delete(handler);
  };
}

/** Current hub status without subscribing. */
export function getHubStatus(): HubStatus {
  return status;
}

/**
 * React binding: starts the shared hub, tracks live connection state, and
 * exposes a stable `subscribe(event, cb)` for typed event wiring.
 */
export function useLiveHub(): {
  status: HubStatus;
  subscribe: typeof subscribeLive;
} {
  const [current, setCurrent] = useState<HubStatus>(status);

  useEffect(() => {
    ensureLiveHubStarted();
    setCurrent(status);
    statusSubscribers.add(setCurrent);
    return () => {
      statusSubscribers.delete(setCurrent);
    };
  }, []);

  return { status: current, subscribe: subscribeLive };
}

/** Convenience hook: subscribe to one hub event for the lifetime of a component. */
export function useLiveEvent<K extends EventName>(event: K, handler: Handler<K>): void {
  const ref = useRef(handler);
  ref.current = handler;
  useEffect(() => {
    ensureLiveHubStarted();
    const unsubscribe = subscribeLive(event, (payload) => ref.current(payload));
    return unsubscribe;
  }, [event]);
}
