import { defineConfig } from "vite";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  server: {
    // Pin the port so the API proxy target + browser URL stay stable; fail loudly
    // (instead of silently moving to 5174) if a stale dev server still holds it.
    host: "localhost",
    port: 5173,
    strictPort: true,
    proxy: {
      "/api": { target: "http://localhost:5285", changeOrigin: true },
      // SignalR live hub — websockets need ws:true for the proxied upgrade.
      "/hub": { target: "http://localhost:5285", changeOrigin: true, ws: true },
    },
  },
});
