import React from "react";
import ReactDOM from "react-dom/client";
import { BrowserRouter } from "react-router-dom";
import "@mantine/core/styles.css";
import { AppThemeProvider } from "./ThemeProvider";
import App from "./App";

// Registered so the browser offers "install as an app", which gives the studio a window without an
// address bar. It caches nothing — see public/service-worker.js. A page served over plain HTTP on
// something other than localhost has no service workers at all, and that is fine: the studio works
// the same, it just cannot be installed.
if ("serviceWorker" in navigator)
  window.addEventListener("load", () => {
    void navigator.serviceWorker.register("/service-worker.js").catch(() => {
      // Not installable here. Nothing else depends on it.
    });
  });

ReactDOM.createRoot(document.getElementById("root")!).render(
  <React.StrictMode>
    <AppThemeProvider>
      <BrowserRouter>
        <App />
      </BrowserRouter>
    </AppThemeProvider>
  </React.StrictMode>
);
