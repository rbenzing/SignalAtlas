import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { act, renderHook } from "@testing-library/react";
import { SdrProvider, useSdr } from "./SdrProvider";

// The mocked `./hackrf` module delegates every method to a `vi.fn()` held in `hoisted` so
// each test can configure connect()/adopt()/startRx()/stop() behavior independently (vi.mock
// factories are hoisted above imports, so the mock fns must come from vi.hoisted too).
const hoisted = vi.hoisted(() => ({
  connectMock: vi.fn(),
  adoptMock: vi.fn(),
  disconnectMock: vi.fn(),
  startRxMock: vi.fn(),
  stopMock: vi.fn(),
  isConnectedMock: vi.fn(),
}));

vi.mock("./hackrf", async () => ({
  // Keep the real pure helpers (computeBasebandFilterBw etc.); only HackRfDevice is faked.
  ...(await vi.importActual<typeof import("./hackrf")>("./hackrf")),
  HackRfDevice: class {
    connect() {
      return hoisted.connectMock();
    }
    adopt(device: unknown) {
      return hoisted.adoptMock(device);
    }
    disconnect() {
      return hoisted.disconnectMock();
    }
    startRx(onData: unknown, onEnd: unknown) {
      return hoisted.startRxMock(onData, onEnd);
    }
    stop() {
      return hoisted.stopMock();
    }
    setSampleRate() {
      return Promise.resolve();
    }
    setBasebandFilter() {
      return Promise.resolve();
    }
    setFrequency() {
      return Promise.resolve();
    }
    setLnaGain() {
      return Promise.resolve();
    }
    setVgaGain() {
      return Promise.resolve();
    }
    setAmpEnable() {
      return Promise.resolve();
    }
    setAntennaEnable() {
      return Promise.resolve();
    }
    get isConnected() {
      return hoisted.isConnectedMock();
    }
  },
  HACKRF_FILTERS: [],
}));

/** Minimal fake WebSocket — jsdom does not provide a real one. Captures every instance so
 * tests can reach in and drive onopen/onclose/onerror by hand. close() mirrors a real socket
 * by invoking onclose synchronously, which is what makes the "intentional stop/disconnect must
 * stay silent" tests meaningful (teardown/stopStreaming must null the handler before close()). */
class FakeWebSocket {
  static instances: FakeWebSocket[] = [];
  binaryType = "";
  bufferedAmount = 0;
  onopen: (() => void) | null = null;
  onclose: (() => void) | null = null;
  onerror: (() => void) | null = null;
  /** Every frame passed to send(), in order — lets tests inspect the JSON config frames sent. */
  sent: unknown[] = [];

  constructor(public url: string) {
    FakeWebSocket.instances.push(this);
  }

  send(data: unknown): void {
    this.sent.push(data);
  }

  close(): void {
    this.onclose?.();
  }
}

const flush = () => new Promise<void>((resolve) => setTimeout(resolve, 0));

/** connect() → "ready" (device adopted, NOT streaming — no socket yet). */
async function connectToReady(result: { current: ReturnType<typeof useSdr> }) {
  let p!: Promise<void>;
  act(() => {
    p = result.current.connect();
  });
  await act(async () => {
    await p;
  });
}

/** From "ready", tune() a frequency to open the socket + start RX, then drive onopen → "streaming". */
async function reachStreaming(
  result: { current: ReturnType<typeof useSdr> },
  centerFreqHz = 915_000_000,
  sampleRateHz = 2_000_000,
): Promise<FakeWebSocket> {
  await connectToReady(result);
  let tp!: Promise<void>;
  act(() => {
    tp = result.current.tune(centerFreqHz, sampleRateHz);
  });
  await act(flush);
  const ws = FakeWebSocket.instances[FakeWebSocket.instances.length - 1];
  await act(async () => {
    ws.onopen?.();
    await tp;
  });
  return ws;
}

beforeEach(() => {
  FakeWebSocket.instances.length = 0;
  hoisted.connectMock.mockReset().mockRejectedValue(new Error("No HackRF selected."));
  hoisted.adoptMock.mockReset();
  hoisted.disconnectMock.mockReset().mockResolvedValue(undefined);
  hoisted.startRxMock.mockReset().mockResolvedValue(undefined);
  hoisted.stopMock.mockReset().mockResolvedValue(undefined);
  hoisted.isConnectedMock.mockReset().mockReturnValue(false);
});

afterEach(() => {
  vi.unstubAllGlobals();
  delete (navigator as unknown as { usb?: unknown }).usb;
});

describe("SdrProvider connect() error handling", () => {
  it("surfaces the error state (not idle) when connect() fails", async () => {
    const { result } = renderHook(() => useSdr(), {
      wrapper: ({ children }) => <SdrProvider>{children}</SdrProvider>,
    });

    await act(async () => {
      await result.current.connect();
    });

    expect(result.current.status).toBe("error");
    expect(result.current.error).toBe("No HackRF selected.");
  });
});

describe("SdrProvider connect() resolves to ready, not streaming", () => {
  it("adopts the device and idles (no socket, no frequency) until a frequency is tuned", async () => {
    vi.stubGlobal("WebSocket", FakeWebSocket);
    hoisted.connectMock.mockResolvedValue({ boardId: 1, firmwareVersion: "1.0", serialNumber: "RDY1" });

    const { result } = renderHook(() => useSdr(), {
      wrapper: ({ children }) => <SdrProvider>{children}</SdrProvider>,
    });

    await connectToReady(result);

    expect(result.current.status).toBe("ready");
    expect(result.current.serial).toBe("RDY1");
    expect(result.current.activeFreqHz).toBeNull();
    // Connecting alone must NOT open an IQ socket or start RX — the radio is not scanning.
    expect(FakeWebSocket.instances.length).toBe(0);
    expect(hoisted.startRxMock).not.toHaveBeenCalled();
  });

  it("starts streaming when a frequency is tuned from ready", async () => {
    vi.stubGlobal("WebSocket", FakeWebSocket);
    hoisted.connectMock.mockResolvedValue({ boardId: 1, firmwareVersion: "1.0", serialNumber: "RDY1" });

    const { result } = renderHook(() => useSdr(), {
      wrapper: ({ children }) => <SdrProvider>{children}</SdrProvider>,
    });

    await reachStreaming(result, 1_090_000_000, 8_000_000);

    expect(result.current.status).toBe("streaming");
    expect(result.current.activeFreqHz).toBe(1_090_000_000);
    expect(hoisted.startRxMock).toHaveBeenCalled();
    expect(FakeWebSocket.instances.length).toBe(1);
  });
});

describe("SdrProvider Stop keeps the device but ends the stream", () => {
  it("stops RX + closes the socket, stays adopted (ready), clears the active frequency", async () => {
    vi.stubGlobal("WebSocket", FakeWebSocket);
    hoisted.connectMock.mockResolvedValue({ boardId: 1, firmwareVersion: "1.0", serialNumber: "STP1" });

    const { result } = renderHook(() => useSdr(), {
      wrapper: ({ children }) => <SdrProvider>{children}</SdrProvider>,
    });

    await reachStreaming(result);
    expect(result.current.status).toBe("streaming");

    await act(async () => {
      await result.current.stop();
    });

    expect(result.current.status).toBe("ready");
    expect(result.current.activeFreqHz).toBeNull();
    expect(result.current.serial).toBe("STP1"); // device identity retained
    expect(hoisted.stopMock).toHaveBeenCalled(); // RX halted...
    expect(hoisted.disconnectMock).not.toHaveBeenCalled(); // ...but USB device NOT released
    expect(result.current.error).toBeNull();
  });

  it("can restart streaming after Stop without re-prompting (device still adopted)", async () => {
    vi.stubGlobal("WebSocket", FakeWebSocket);
    hoisted.connectMock.mockResolvedValue({ boardId: 1, firmwareVersion: "1.0", serialNumber: "STP2" });

    const { result } = renderHook(() => useSdr(), {
      wrapper: ({ children }) => <SdrProvider>{children}</SdrProvider>,
    });

    await reachStreaming(result);
    await act(async () => {
      await result.current.stop();
    });

    // Tune again → new stream, WITHOUT another connect()/chooser prompt.
    let tp!: Promise<void>;
    act(() => {
      tp = result.current.tune(162_400_000, 2_000_000);
    });
    await act(flush);
    const ws = FakeWebSocket.instances[FakeWebSocket.instances.length - 1];
    await act(async () => {
      ws.onopen?.();
      await tp;
    });

    expect(result.current.status).toBe("streaming");
    expect(result.current.activeFreqHz).toBe(162_400_000);
    expect(hoisted.connectMock).toHaveBeenCalledTimes(1); // only the original connect
  });
});

describe("SdrProvider config frame RX-config provenance", () => {
  it("sends lnaDb/vgaDb/ampEnable/basebandBwHz/biasTee in the initial config frame", async () => {
    vi.stubGlobal("WebSocket", FakeWebSocket);
    hoisted.connectMock.mockResolvedValue({ boardId: 1, firmwareVersion: "1.0", serialNumber: "ABC123" });

    const { result } = renderHook(() => useSdr(), {
      wrapper: ({ children }) => <SdrProvider>{children}</SdrProvider>,
    });

    const ws = await reachStreaming(result);

    // First sent frame is the JSON config frame (stringified, since IqSocket.sendConfig does
    // JSON.stringify(cfg) before handing it to the raw socket).
    const configFrame = JSON.parse(ws.sent[0] as string);
    expect(configFrame).toMatchObject({
      lnaDb: 16,
      vgaDb: 20,
      ampEnable: false,
      basebandBwHz: 1_750_000, // computeBasebandFilterBw(2 MHz) rounds down to the 1.75 MHz HackRF width
      biasTee: false,
    });
  });

  it("re-sends the current RX-config fields on a setTuning() reconfig frame", async () => {
    vi.stubGlobal("WebSocket", FakeWebSocket);
    hoisted.connectMock.mockResolvedValue({ boardId: 1, firmwareVersion: "1.0", serialNumber: "ABC123" });
    hoisted.isConnectedMock.mockReturnValue(true);

    const { result } = renderHook(() => useSdr(), {
      wrapper: ({ children }) => <SdrProvider>{children}</SdrProvider>,
    });

    const ws = await reachStreaming(result);

    await act(async () => {
      await result.current.setTuning({ lnaGain: 32, biasTee: true });
    });

    const lastFrame = JSON.parse(ws.sent[ws.sent.length - 1] as string);
    expect(lastFrame).toMatchObject({
      lnaDb: 32,
      vgaDb: 20,
      ampEnable: false,
      basebandBwHz: 1_750_000, // actual rounded HackRF filter width for a 2 MHz sample rate
      biasTee: true,
    });
  });
});

describe("SdrProvider mid-stream error handling (Fix 1)", () => {
  it("tears down the device (releasing the USB interface) before surfacing the error", async () => {
    vi.stubGlobal("WebSocket", FakeWebSocket);
    hoisted.connectMock.mockResolvedValue({ boardId: 1, firmwareVersion: "1.0", serialNumber: "ABC123" });
    // Capture onEnd so the test controls WHEN the mid-stream failure fires: assert "streaming"
    // first, then trigger onEnd deterministically. (A setTimeout(0) race here was flaky — it
    // could fire before the "streaming" assertion.) Mirrors the real startRx resolving
    // immediately while the readLoop runs on in the background.
    let capturedOnEnd: ((err?: Error) => void) | undefined;
    hoisted.startRxMock.mockImplementation((_onData: unknown, onEnd?: (err?: Error) => void) => {
      capturedOnEnd = onEnd;
      return Promise.resolve();
    });

    const { result } = renderHook(() => useSdr(), {
      wrapper: ({ children }) => <SdrProvider>{children}</SdrProvider>,
    });

    await reachStreaming(result);
    expect(result.current.status).toBe("streaming");

    // Trigger the mid-stream failure deterministically and let its `await teardown()` resolve.
    await act(async () => {
      capturedOnEnd?.(new Error("stream died"));
      await flush();
    });

    expect(result.current.status).toBe("error");
    expect(result.current.error).toBe("stream died");
    expect(hoisted.disconnectMock).toHaveBeenCalled();
  });
});

describe("SdrProvider unexpected socket close handling (Fix 2)", () => {
  it("surfaces an error when the IQ socket closes unexpectedly after streaming starts", async () => {
    vi.stubGlobal("WebSocket", FakeWebSocket);
    hoisted.connectMock.mockResolvedValue({ boardId: 1, firmwareVersion: "1.0", serialNumber: "ABC123" });

    const { result } = renderHook(() => useSdr(), {
      wrapper: ({ children }) => <SdrProvider>{children}</SdrProvider>,
    });

    const ws = await reachStreaming(result);
    expect(result.current.status).toBe("streaming");

    // Simulate the browser firing an unexpected close event (backend restart / network
    // drop) — NOT triggered via our own teardown(), so the handlers are still attached.
    act(() => {
      ws.onclose?.();
    });

    expect(result.current.status).toBe("error");
    expect(result.current.error).toBe("IQ stream disconnected.");
  });

  it("does not surface an error when disconnect() is called intentionally", async () => {
    vi.stubGlobal("WebSocket", FakeWebSocket);
    hoisted.connectMock.mockResolvedValue({ boardId: 1, firmwareVersion: "1.0", serialNumber: "XYZ" });

    const { result } = renderHook(() => useSdr(), {
      wrapper: ({ children }) => <SdrProvider>{children}</SdrProvider>,
    });

    await reachStreaming(result);
    expect(result.current.status).toBe("streaming");

    await act(async () => {
      await result.current.disconnect();
    });

    // teardown() detaches ws.onclose before calling ws.close(), so FakeWebSocket.close()
    // (which synchronously invokes onclose) must be a no-op here — no error dispatch.
    expect(result.current.status).toBe("idle");
    expect(result.current.error).toBeNull();
  });
});

describe("SdrProvider auto-reconnect on mount (Fix 3)", () => {
  it("silently adopts a previously-authorized HackRF to ready (no prompt, no auto-scan)", async () => {
    vi.stubGlobal("WebSocket", FakeWebSocket);
    const getDevicesMock = vi.fn().mockResolvedValue([{ vendorId: 0x1d50, productId: 0x6089 }]);
    const requestDeviceMock = vi.fn();
    (navigator as unknown as { usb: unknown }).usb = {
      getDevices: getDevicesMock,
      requestDevice: requestDeviceMock,
    };
    hoisted.adoptMock.mockResolvedValue({ boardId: 1, firmwareVersion: "2.0", serialNumber: "AUTO123" });

    const { result } = renderHook(() => useSdr(), {
      wrapper: ({ children }) => <SdrProvider>{children}</SdrProvider>,
    });

    // Let the mount effect run: getDevices() -> adopt() -> ready (NOT streaming).
    await act(flush);

    expect(result.current.status).toBe("ready");
    expect(result.current.serial).toBe("AUTO123");
    expect(result.current.activeFreqHz).toBeNull();
    expect(hoisted.adoptMock).toHaveBeenCalledWith({ vendorId: 0x1d50, productId: 0x6089 });
    expect(requestDeviceMock).not.toHaveBeenCalled();
    expect(hoisted.connectMock).not.toHaveBeenCalled();
    // No auto-scan: no socket opened, RX never started until a frequency is selected.
    expect(FakeWebSocket.instances.length).toBe(0);
    expect(hoisted.startRxMock).not.toHaveBeenCalled();
  });

  it("stays idle when no previously-authorized device is returned", async () => {
    const getDevicesMock = vi.fn().mockResolvedValue([]);
    (navigator as unknown as { usb: unknown }).usb = { getDevices: getDevicesMock, requestDevice: vi.fn() };

    const { result } = renderHook(() => useSdr(), {
      wrapper: ({ children }) => <SdrProvider>{children}</SdrProvider>,
    });

    await act(flush);

    expect(result.current.status).toBe("idle");
    expect(hoisted.adoptMock).not.toHaveBeenCalled();
  });
});
