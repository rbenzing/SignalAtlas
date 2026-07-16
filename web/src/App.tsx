import { BrowserRouter, Routes, Route } from "react-router-dom";
import AppShell from "./components/AppShell";
import Dashboard from "./views/Dashboard";
import LiveSpectrum from "./views/LiveSpectrum";
import RfMap from "./views/RfMap";
import Emitters from "./views/Emitters";
import Devices from "./views/Devices";
import Alerts from "./views/Alerts";

export default function App() {
  return (
    <BrowserRouter>
      <AppShell>
        <Routes>
          <Route path="/" element={<Dashboard />} />
          <Route path="/spectrum" element={<LiveSpectrum />} />
          <Route path="/map" element={<RfMap />} />
          <Route path="/emitters" element={<Emitters />} />
          <Route path="/devices" element={<Devices />} />
          <Route path="/alerts" element={<Alerts />} />
        </Routes>
      </AppShell>
    </BrowserRouter>
  );
}
