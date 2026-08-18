import { Route, Routes } from "react-router-dom";
import { AuthGate } from "./auth/AuthGate";
import { AppShellFrame } from "./components/AppShellFrame";
import { ConnectionsPage } from "./connections/ConnectionsPage";
import { DockShell } from "./dock/DockShell";

export default function App() {
  return (
    <AuthGate>
      <AppShellFrame>
        <Routes>
          <Route path="/" element={<DockShell />} />
          <Route path="/connections" element={<ConnectionsPage />} />
        </Routes>
      </AppShellFrame>
    </AuthGate>
  );
}
