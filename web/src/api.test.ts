import { describe, it, expect, vi, afterEach } from "vitest";
import { getPaged, type ApiEnvelope, type Signal } from "./api";

function envelopeResponse<T>(env: ApiEnvelope<T>): Response {
  return {
    ok: true,
    status: 200,
    json: async () => env,
  } as unknown as Response;
}

afterEach(() => {
  vi.unstubAllGlobals();
});

describe("getPaged", () => {
  it("fetches the bare path (no ?cursor=) when no cursor is supplied", async () => {
    const items: Signal[] = [];
    const fetchMock = vi.fn().mockResolvedValue(
      envelopeResponse<Signal[]>({
        schemaVersion: "v1",
        correlationId: "11111111-1111-1111-1111-111111111111",
        payload: items,
        nextCursor: "MQ==",
      }),
    );
    vi.stubGlobal("fetch", fetchMock);

    const result = await getPaged<Signal[]>("/signals");

    expect(fetchMock).toHaveBeenCalledWith("/api/v1/signals");
    expect(result).toEqual({ items, nextCursor: "MQ==" });
  });

  it("appends ?cursor= (URL-encoded) when a cursor is supplied", async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      envelopeResponse<Signal[]>({
        schemaVersion: "v1",
        correlationId: "11111111-1111-1111-1111-111111111111",
        payload: [],
        nextCursor: null,
      }),
    );
    vi.stubGlobal("fetch", fetchMock);

    const result = await getPaged<Signal[]>("/signals", "MQ==");

    expect(fetchMock).toHaveBeenCalledWith("/api/v1/signals?cursor=MQ%3D%3D");
    expect(result).toEqual({ items: [], nextCursor: null });
  });

  it("normalizes a missing/undefined nextCursor to null", async () => {
    const fetchMock = vi.fn().mockResolvedValue(
      envelopeResponse<Signal[]>({
        schemaVersion: "v1",
        correlationId: "11111111-1111-1111-1111-111111111111",
        payload: [],
      }),
    );
    vi.stubGlobal("fetch", fetchMock);

    const result = await getPaged<Signal[]>("/signals");

    expect(result.nextCursor).toBeNull();
  });

  it("throws when the response is not ok", async () => {
    const fetchMock = vi.fn().mockResolvedValue({ ok: false, status: 500 } as Response);
    vi.stubGlobal("fetch", fetchMock);

    await expect(getPaged<Signal[]>("/signals")).rejects.toThrow(/500/);
  });
});
