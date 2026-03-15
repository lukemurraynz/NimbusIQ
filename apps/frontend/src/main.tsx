import { StrictMode } from "react";
import { createRoot } from "react-dom/client";
import { BrowserRouter } from "react-router-dom";
import { FluentProvider } from "@fluentui/react-components";
import { MsalProvider } from "@azure/msal-react";
import "./index.css";
import App from "./App.tsx";
import { msalInstance } from "./auth/msal";
import { azureV9LightTheme } from "./theme/azureV9Theme.ts";
import { ErrorBoundary } from "./components/ErrorBoundary";
import { ToastProvider } from "./components/ToastProvider";

const app = (
  <FluentProvider theme={azureV9LightTheme}>
    <ErrorBoundary>
      <ToastProvider>
        <BrowserRouter>
          <App />
        </BrowserRouter>
      </ToastProvider>
    </ErrorBoundary>
  </FluentProvider>
);

createRoot(document.getElementById("root")!).render(
  <StrictMode>
    {msalInstance ? (
      <MsalProvider instance={msalInstance}>{app}</MsalProvider>
    ) : (
      app
    )}
  </StrictMode>,
);
