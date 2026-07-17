import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import App from "./App";
import { ColorModeProvider } from "./theme/ColorModeContext";
import { LiveProvider } from "./live/LiveProvider";
import { SdrProvider } from "./sdr/SdrProvider";

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    <ColorModeProvider>
      <LiveProvider>
        <SdrProvider>
          <App />
        </SdrProvider>
      </LiveProvider>
    </ColorModeProvider>
  </StrictMode>,
);
