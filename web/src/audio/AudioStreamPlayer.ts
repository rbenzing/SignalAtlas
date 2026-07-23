/**
 * RF Audio Player streaming client (WS-D3, design doc §2/§4).
 *
 * Opens the ungated `/audio` WebSocket, sends a JSON config frame `{mode, enabled}`, and plays
 * incoming binary PCM16LE mono @ 25 kHz frames (SPEC: AudioRateHz = 25_000, integer-decimated
 * from the block sample rate — see design doc §2) through the Web Audio API. Playback schedules
 * back-to-back `AudioBufferSourceNode`s on a single running `AudioContext`, tracking
 * `nextStartTime` so frames queue seamlessly; if playback falls behind (buffer underrun), it
 * resyncs to `ctx.currentTime` rather than racing to catch up.
 *
 * Landmine #1 (frontend/API type drift): `/audio` is a RAW BINARY PCM stream, not a JSON
 * envelope — never route it through `getEnvelope`/`api.ts` JSON helpers.
 */

export type AudioMode = "wbfm" | "nbfm" | "am";

/** Bytes-per-sample for PCM16LE. */
const BYTES_PER_SAMPLE = 2;

/** Server streams mono PCM16LE at a fixed 25 kHz (design doc §2: AudioRateHz = 2 MS/s ÷ 80). */
const AUDIO_SAMPLE_RATE_HZ = 25_000;

export interface AudioStreamPlayerOptions {
  /** Called with the RMS (0..1) of each decoded PCM chunk, for a level meter. */
  onLevel?: (rms: number) => void;
}

function audioSocketUrl(): string {
  const proto = window.location.protocol === "https:" ? "wss" : "ws";
  return `${proto}://${window.location.host}/audio`;
}

/**
 * Streams and plays server-demodulated audio for the currently-tuned frequency.
 * One instance per playback session — call `stop()` to fully tear down before discarding.
 */
export class AudioStreamPlayer {
  private ws: WebSocket | null = null;
  private ctx: AudioContext | null = null;
  private gainNode: GainNode | null = null;
  private nextStartTime = 0;
  private mode: AudioMode = "wbfm";
  private volume = 1;
  private readonly onLevel?: (rms: number) => void;

  constructor(options: AudioStreamPlayerOptions = {}) {
    this.onLevel = options.onLevel;
  }

  /** True while a socket is open (playing or attempting to). */
  get isPlaying(): boolean {
    return this.ws !== null;
  }

  /** Opens the `/audio` socket, sends the initial config frame, and starts playback. */
  play(mode: AudioMode): void {
    this.stop(); // idempotent: tear down any previous session first.
    this.mode = mode;

    const ctx = new AudioContext();
    this.ctx = ctx;
    this.nextStartTime = ctx.currentTime;

    const gainNode = ctx.createGain();
    gainNode.gain.value = this.volume;
    gainNode.connect(ctx.destination);
    this.gainNode = gainNode;

    const ws = new WebSocket(audioSocketUrl());
    ws.binaryType = "arraybuffer";
    this.ws = ws;

    ws.onopen = () => this.sendConfig(true);
    ws.onmessage = (ev) => this.handleMessage(ev);
    // Best-effort: a dropped/failed socket just ends playback silently (no auto-reconnect —
    // the operator presses Play again). Mirrors the ungated /audio posture (design doc §3).
    ws.onclose = () => this.teardownSocketOnly();
    ws.onerror = () => this.teardownSocketOnly();
  }

  /** Switch demod mode on an already-open stream (no reconnect). */
  setMode(mode: AudioMode): void {
    this.mode = mode;
    if (this.ws && this.ws.readyState === WebSocket.OPEN) {
      this.sendConfig(true);
    }
  }

  /** Master volume, 0..1. */
  setVolume(volume: number): void {
    this.volume = Math.min(1, Math.max(0, volume));
    if (this.gainNode) {
      this.gainNode.gain.value = this.volume;
    }
  }

  /** Ends playback: tells the server to stop, closes the socket, and tears down the AudioContext. */
  stop(): void {
    if (this.ws) {
      try {
        this.sendConfig(false);
      } catch {
        /* socket may already be closing */
      }
      this.ws.onopen = null;
      this.ws.onmessage = null;
      this.ws.onclose = null;
      this.ws.onerror = null;
      try {
        this.ws.close();
      } catch {
        /* ignore */
      }
      this.ws = null;
    }
    if (this.ctx) {
      try {
        void this.ctx.close();
      } catch {
        /* ignore */
      }
      this.ctx = null;
    }
    this.gainNode = null;
    this.nextStartTime = 0;
  }

  private sendConfig(enabled: boolean): void {
    this.ws?.send(JSON.stringify({ mode: this.mode, enabled }));
  }

  /** Socket dropped out from under us (server-side close/error) — stop cleanly without
   * re-sending a config frame on an already-dead socket. */
  private teardownSocketOnly(): void {
    if (this.ws) {
      this.ws.onopen = null;
      this.ws.onmessage = null;
      this.ws.onclose = null;
      this.ws.onerror = null;
      this.ws = null;
    }
    if (this.ctx) {
      try {
        void this.ctx.close();
      } catch {
        /* ignore */
      }
      this.ctx = null;
    }
    this.gainNode = null;
    this.nextStartTime = 0;
  }

  private handleMessage(ev: MessageEvent): void {
    if (!(ev.data instanceof ArrayBuffer)) return; // ignore any stray text frame
    const ctx = this.ctx;
    const gainNode = this.gainNode;
    if (!ctx || !gainNode) return;

    const int16 = new Int16Array(ev.data);
    const frameCount = int16.length;
    if (frameCount === 0) return;

    const float32 = new Float32Array(frameCount);
    let sumSquares = 0;
    for (let i = 0; i < frameCount; i++) {
      const s = int16[i] / 32768;
      float32[i] = s;
      sumSquares += s * s;
    }

    const buffer = ctx.createBuffer(1, frameCount, AUDIO_SAMPLE_RATE_HZ);
    if (typeof buffer.copyToChannel === "function") {
      buffer.copyToChannel(float32, 0);
    } else {
      buffer.getChannelData(0).set(float32);
    }

    const source = ctx.createBufferSource();
    source.buffer = buffer;
    source.connect(gainNode);

    // Chain back-to-back: if we've fallen behind (underrun), resync to "now" instead of
    // scheduling a burst of overdue buffers.
    const startAt = Math.max(this.nextStartTime, ctx.currentTime);
    source.start(startAt);
    this.nextStartTime = startAt + buffer.duration;

    if (this.onLevel) {
      this.onLevel(Math.sqrt(sumSquares / frameCount));
    }
  }
}

/** Exposed for tests that need to compute expected buffer duration without re-deriving it. */
export const AUDIO_RATE_HZ = AUDIO_SAMPLE_RATE_HZ;
export const PCM_BYTES_PER_SAMPLE = BYTES_PER_SAMPLE;
