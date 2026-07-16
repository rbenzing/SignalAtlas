export interface IqStreamConfig {
  type: "config";
  centerFreqHz: number;
  sampleRateHz: number;
  samplesPerBlock: number;
  collectorId: string;
}

/** Minimal surface of the browser WebSocket we depend on (so tests can fake it). */
export interface IqSocketLike {
  readonly bufferedAmount: number;
  send(data: string | ArrayBufferView): void;
}

const DEFAULT_MAX_BUFFERED_BYTES = 1_000_000; // ~0.25 s at 2 MS/s int8 I/Q; drop-oldest beyond this.

/**
 * Frames IQ over a WebSocket: one JSON text config frame, then binary int8 frames. Applies
 * drop-oldest backpressure — if the socket's send buffer is backed up, the freshest block is
 * dropped rather than queued unboundedly (mirrors the backend BoundedChannel DropOldest).
 */
export class IqSocket {
  private _drops = 0;

  constructor(
    private readonly socket: IqSocketLike,
    private readonly maxBufferedBytes = DEFAULT_MAX_BUFFERED_BYTES,
  ) {}

  get drops(): number {
    return this._drops;
  }

  sendConfig(cfg: IqStreamConfig): void {
    this.socket.send(JSON.stringify(cfg));
  }

  /** Returns true if sent, false if dropped due to backpressure. */
  sendIq(samples: Int8Array): boolean {
    if (this.socket.bufferedAmount > this.maxBufferedBytes) {
      this._drops++;
      return false;
    }
    this.socket.send(samples);
    return true;
  }
}
