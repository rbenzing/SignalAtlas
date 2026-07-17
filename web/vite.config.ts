import { defineConfig, createLogger, type ProxyOptions } from "vite";
import react from "@vitejs/plugin-react";
import type { Socket } from "node:net";
import type { ServerResponse } from "node:http";

const API_TARGET = "http://localhost:5285";

// During dev the .NET API takes a few seconds to build + boot, while Vite starts instantly and the
// browser immediately polls every /api route. Those requests hit a not-yet-listening port. Two things
// make that quiet instead of a per-request ECONNREFUSED stack-trace flood (purely a dev-server nicety,
// no app/API behavior change):
//
// 1. A throttled custom logger that collapses Vite's own "http proxy error" spam into one line every
//    few seconds while the backend is starting.
// 2. A proxy error handler that returns a clean 503 so the frontend's fetch fails gracefully (its
//    polling just retries) rather than seeing a hung/reset socket.

const logger = createLogger();
const baseError = logger.error;
let lastProxyLog = 0;
logger.error = (msg, options) => {
  if (typeof msg === "string" && msg.includes("http proxy error")) {
    const now = Date.now();
    if (now - lastProxyLog > 3000) {
      lastProxyLog = now;
      baseError(`[proxy] backend not reachable at ${API_TARGET} yet (still starting up?) — retrying…`, options);
    }
    return; // swallow the repeated full stack traces
  }
  baseError(msg, options);
};

function backendProxy(ws = false): ProxyOptions {
  return {
    target: API_TARGET,
    changeOrigin: true,
    ws,
    configure: (proxy) => {
      proxy.on("error", (_err, _req, res) => {
        // res is a ServerResponse for HTTP requests, or a raw Socket for WS upgrades.
        if (res && "writeHead" in res) {
          const httpRes = res as ServerResponse;
          if (!httpRes.headersSent) {
            httpRes.writeHead(503, { "content-type": "application/json" });
            httpRes.end(JSON.stringify({ error: "backend_unavailable" }));
          }
        } else if (res) {
          (res as Socket).destroy();
        }
      });
    },
  };
}

export default defineConfig({
  plugins: [react()],
  customLogger: logger,
  server: {
    // Pin the port so the API proxy target + browser URL stay stable; fail loudly
    // (instead of silently moving to 5174) if a stale dev server still holds it.
    host: "localhost",
    port: 5173,
    strictPort: true,
    proxy: {
      "/api": backendProxy(),
      // SignalR live hub — websockets need ws:true for the proxied upgrade.
      "/hub": backendProxy(true),
      // Browser WebUSB HackRF IQ ingress — websocket, needs ws:true for the upgrade.
      "/ingest": backendProxy(true),
    },
  },
});
