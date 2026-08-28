import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";

export default defineConfig({
  plugins: [react()],
  server: { proxy: { "/api": "http://localhost:5000" } },
  // The jsdom gaps Mantine expects, in one place rather than copied into every component test.
  test: { setupFiles: ["src/test-setup.ts"] },
});
