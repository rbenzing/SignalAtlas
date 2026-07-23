import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { AudioStreamPlayer } from "./AudioStreamPlayer";

/** Minimal fake WebSocket — jsdom does not provide a real one. Mirrors the FakeWebSocket in
 * web/src/sdr/SdrProvider.test.tsx: captures every instance + every frame sent so tests can
 * inspect the JSON config frames and drive onopen/onmessage/onclose by hand. */
class FakeWebSocket {
  static instances: FakeWebSocket[] = [];
  static readonly OPEN = 1;
  static readonly CLOSED = 3;

  binaryType = "";
  readyState = FakeWebSocket.OPEN;
  onopen: (() => void) | null = null;
  onmessage: ((ev: { data: unknown }) => void) | null = null;
  onclose: (() => void) | null = null;
  onerror: (() => void) | null = null;
  sent: unknown[] = [];
  closed = false;

  constructor(public url: string) {
    FakeWebSocket.instances.push(this);
  }

  send(data: unknown): void {
    this.sent.push(data);
  }

  close(): void {
    this.closed = true;
    this.readyState = FakeWebSocket.CLOSED;
  }
}

/** Stubbed Web Audio graph — jsdom has no AudioContext. Tracks every node created so tests
 * can assert buffer sources were created + started. */
class FakeGainNode {
  gain = { value: 1 };
  connect(): void {
    /* no-op */
  }
}

class FakeAudioBuffer {
  readonly duration: number;
  private readonly data: Float32Array;

  constructor(
    public numberOfChannels: number,
    public length: number,
    public sampleRate: number,
  ) {
    this.duration = length / sampleRate;
    this.data = new Float32Array(length);
  }

  copyToChannel(source: Float32Array): void {
    this.data.set(source);
  }

  getChannelData(): Float32Array {
    return this.data;
  }
}

class FakeAudioBufferSourceNode {
  static instances: FakeAudioBufferSourceNode[] = [];
  buffer: FakeAudioBuffer | null = null;
  started = false;
  startedAt = 0;

  constructor() {
    FakeAudioBufferSourceNode.instances.push(this);
  }

  connect(): void {
    /* no-op */
  }

  start(when = 0): void {
    this.started = true;
    this.startedAt = when;
  }
}

class FakeAudioContext {
  static instances: FakeAudioContext[] = [];
  currentTime = 0;
  destination = {};
  closed = false;

  constructor() {
    FakeAudioContext.instances.push(this);
  }

  createGain(): FakeGainNode {
    return new FakeGainNode();
  }

  createBuffer(channels: number, length: number, sampleRate: number): FakeAudioBuffer {
    return new FakeAudioBuffer(channels, length, sampleRate);
  }

  createBufferSource(): FakeAudioBufferSourceNode {
    return new FakeAudioBufferSourceNode();
  }

  close(): Promise<void> {
    this.closed = true;
    return Promise.resolve();
  }
}

/** Builds a PCM16LE mono ArrayBuffer from plain sample values (-32768..32767). */
function pcm16(samples: number[]): ArrayBuffer {
  const buf = new ArrayBuffer(samples.length * 2);
  const view = new DataView(buf);
  samples.forEach((s, i) => view.setInt16(i * 2, s, true));
  return buf;
}

beforeEach(() => {
  FakeWebSocket.instances.length = 0;
  FakeAudioBufferSourceNode.instances.length = 0;
  FakeAudioContext.instances.length = 0;
  vi.stubGlobal("WebSocket", FakeWebSocket);
  vi.stubGlobal("AudioContext", FakeAudioContext);
});

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("AudioStreamPlayer.play", () => {
  it("opens the /audio socket and sends the config frame on open", () => {
    const player = new AudioStreamPlayer();
    player.play("am");

    expect(FakeWebSocket.instances.length).toBe(1);
    const ws = FakeWebSocket.instances[0];
    expect(ws.url).toMatch(/\/audio$/);

    ws.onopen?.();

    expect(ws.sent.length).toBe(1);
    expect(JSON.parse(ws.sent[0] as string)).toEqual({ mode: "am", enabled: true });
  });

  it("sends the config frame for the new usb/lsb/cw modes", () => {
    const player = new AudioStreamPlayer();
    player.play("usb");

    const ws = FakeWebSocket.instances[0];
    ws.onopen?.();

    expect(JSON.parse(ws.sent[0] as string)).toEqual({ mode: "usb", enabled: true });
  });

  it("creates and starts a buffer source for an incoming binary PCM frame", () => {
    const player = new AudioStreamPlayer();
    player.play("wbfm");
    const ws = FakeWebSocket.instances[0];
    ws.onopen?.();

    const frame = pcm16([0, 16384, -16384, 32767]);
    ws.onmessage?.({ data: frame });

    expect(FakeAudioBufferSourceNode.instances.length).toBe(1);
    const source = FakeAudioBufferSourceNode.instances[0];
    expect(source.started).toBe(true);
    expect(source.buffer?.length).toBe(4);
    expect(source.buffer?.sampleRate).toBe(25_000);
  });

  it("ignores a non-ArrayBuffer message (e.g. a stray text frame)", () => {
    const player = new AudioStreamPlayer();
    player.play("nbfm");
    const ws = FakeWebSocket.instances[0];
    ws.onopen?.();

    ws.onmessage?.({ data: "not binary" });

    expect(FakeAudioBufferSourceNode.instances.length).toBe(0);
  });

  it("reports RMS level via onLevel for a decoded PCM chunk", () => {
    const onLevel = vi.fn();
    const player = new AudioStreamPlayer({ onLevel });
    player.play("am");
    const ws = FakeWebSocket.instances[0];
    ws.onopen?.();

    // Full-scale square wave -> RMS should be close to 1 (32767/32768).
    ws.onmessage?.({ data: pcm16([32767, -32768, 32767, -32768]) });

    expect(onLevel).toHaveBeenCalledTimes(1);
    expect(onLevel.mock.calls[0][0]).toBeGreaterThan(0.99);
  });
});

describe("AudioStreamPlayer.setMode", () => {
  it("re-sends the config frame with the new mode on an open socket, without reconnecting", () => {
    const player = new AudioStreamPlayer();
    player.play("wbfm");
    const ws = FakeWebSocket.instances[0];
    ws.onopen?.();
    ws.sent.length = 0; // clear the initial config frame

    player.setMode("nbfm");

    expect(FakeWebSocket.instances.length).toBe(1); // no new socket
    expect(JSON.parse(ws.sent[0] as string)).toEqual({ mode: "nbfm", enabled: true });
  });
});

describe("AudioStreamPlayer.stop", () => {
  it("sends enabled:false, closes the socket, and tears down the AudioContext", () => {
    const player = new AudioStreamPlayer();
    player.play("am");
    const ws = FakeWebSocket.instances[0];
    ws.onopen?.();
    ws.sent.length = 0;

    player.stop();

    expect(JSON.parse(ws.sent[0] as string)).toEqual({ mode: "am", enabled: false });
    expect(ws.closed).toBe(true);
    expect(FakeAudioContext.instances[0].closed).toBe(true);
    expect(player.isPlaying).toBe(false);
  });

  it("is safe to call when nothing is playing", () => {
    const player = new AudioStreamPlayer();
    expect(() => player.stop()).not.toThrow();
  });
});
