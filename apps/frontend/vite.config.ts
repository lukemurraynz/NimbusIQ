import { defineConfig } from "vitest/config";
import react from "@vitejs/plugin-react";

// https://vite.dev/config/
export default defineConfig({
  plugins: [react()],
  test: {
    environment: "jsdom",
    setupFiles: "./src/test/setup.ts",
    globals: true,
    clearMocks: true,
  },
  build: {
    // Fluent UI + recharts are large vendor bundles; 750 kB is acceptable for gzipped pages
    chunkSizeWarningLimit: 750,
    rollupOptions: {
      output: {
        manualChunks(id) {
          if (id.includes("node_modules")) {
            // Charting libraries — loaded lazily on drift/timeline pages
            if (
              id.includes("recharts") ||
              id.includes("d3-") ||
              id.includes("d3/")
            ) {
              return "charts";
            }
            // Fluent UI icons — separate from components to allow tree-shaking
            if (id.includes("@fluentui/react-icons")) {
              return "fluent-icons";
            }
            // Fluent UI components + tokens
            if (id.includes("@fluentui")) {
              return "fluent";
            }
            // OpenTelemetry — instrumentation, not on critical path
            if (id.includes("@opentelemetry")) {
              return "otel";
            }
            // Core React runtime
            if (
              id.includes("react-dom") ||
              id.includes("react-router") ||
              id.includes("react/")
            ) {
              return "vendor";
            }
          }
        },
      },
    },
  },
});
