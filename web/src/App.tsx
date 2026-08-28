import { Route, Routes } from "react-router-dom";
import { AuthGate } from "./auth/AuthGate";
import { AppShellFrame } from "./components/AppShellFrame";
import { ConnectionsPage } from "./connections/ConnectionsPage";
import { DockShell } from "./dock/DockShell";
import { ReportPage } from "./reports/ReportPage";
import { SharePage } from "./share/SharePage";

export default function App() {
  return (
    <Routes>
      {/* Outside the login gate and outside the shell: a shared result is for somebody who may not
          have an account here, and there is nothing on the page for them to navigate. The server
          decides whether the link opens without a login. */}
      <Route path="/share/:id" element={<SharePage />} />

      <Route path="*" element={
        <AuthGate>
          <AppShellFrame>
            <Routes>
              <Route path="/" element={<DockShell />} />
              <Route path="/connections" element={<ConnectionsPage />} />
              {/* A saved query as a form: inside the login gate, because it reads the database —
                  but outside the dock, because the person pressing it is not here to write SQL. */}
              <Route path="/report" element={<ReportPage />} />
              <Route path="/report/:id" element={<ReportPage />} />
            </Routes>
          </AppShellFrame>
        </AuthGate>
      } />
    </Routes>
  );
}
