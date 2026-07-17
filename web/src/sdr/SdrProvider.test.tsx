import { describe, it, expect, vi } from "vitest";
import { act, renderHook } from "@testing-library/react";
import { SdrProvider, useSdr } from "./SdrProvider";

// Mock the WebUSB driver so connect() rejects before ever touching WebUSB/WebSocket
// globals (which jsdom does not provide). This isolates the provider's orchestration
// logic (Fix 1: a failed connect() must surface `status: "error"`, not reset to idle).
vi.mock("./hackrf", () => ({
  HackRfDevice: class {
    async connect(): Promise<never> {
      throw new Error("No HackRF selected.");
    }
    async disconnect(): Promise<void> {
      /* no-op */
    }
    get isConnected() {
      return false;
    }
  },
  HACKRF_FILTERS: [],
}));

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
