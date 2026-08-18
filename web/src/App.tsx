import { AuthGate } from "./auth/AuthGate";
import { AppShellFrame } from "./components/AppShellFrame";
import { ConnectionsPage } from "./connections/ConnectionsPage";

export default function App() {
  return (
    <AuthGate>
      <AppShellFrame>
        <ConnectionsPage />
      </AppShellFrame>
    </AuthGate>
  );
}
