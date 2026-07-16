import { describe, it, expect } from "vitest";
import { IqSocket, type IqSocketLike, type IqStreamConfig } from "./iqSocket";

function fakeSocket(bufferedAmount = 0): IqSocketLike & { sent: Array<string | ArrayBufferView> } {
  return { bufferedAmount, sent: [], send(data) { this.sent.push(data); } };
}

const cfg: IqStreamConfig = {
  type: "config", centerFreqHz: 915e6, sampleRateHz: 2e6, samplesPerBlock: 8192, collectorId: "web-hackrf-1",
};

describe("IqSocket", () => {
  it("sends the config as a JSON text frame", () => {
    const ws = fakeSocket();
    new IqSocket(ws).sendConfig(cfg);
    expect(JSON.parse(ws.sent[0] as string)).toEqual(cfg);
  });

  it("sends IQ as a binary frame when buffer is below the cap", () => {
    const ws = fakeSocket(0);
    const ok = new IqSocket(ws, 1_000_000).sendIq(new Int8Array([1, 2, 3, 4]));
    expect(ok).toBe(true);
    expect(ws.sent[0]).toBeInstanceOf(Int8Array);
  });

  it("drops (does not send) IQ when bufferedAmount exceeds the cap", () => {
    const ws = fakeSocket(2_000_000);
    const sock = new IqSocket(ws, 1_000_000);
    const ok = sock.sendIq(new Int8Array([1, 2, 3, 4]));
    expect(ok).toBe(false);
    expect(ws.sent).toHaveLength(0);
    expect(sock.drops).toBe(1);
  });
});
