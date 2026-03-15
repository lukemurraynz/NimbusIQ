/**
 * T027: Frontend API Proxy Server
 *
 * Proxies /api/v1/* requests to Control Plane API
 * This allows the frontend SPA to call the API with CORS handling
 * In development mode, the API uses ATLAS_ALLOW_ANONYMOUS=true for read access
 */

import express, { Request, Response } from "express";
import path from "path";

// Prevent silent crashes — log and exit so the container restarts cleanly
process.on("uncaughtException", (err) => {
  console.error("Uncaught exception — shutting down:", err);
  process.exit(1);
});
process.on("unhandledRejection", (reason) => {
  console.error("Unhandled rejection — shutting down:", reason);
  process.exit(1);
});

const PROXY_TIMEOUT_MS = 30_000;
// SignalR long polling holds the connection open waiting for messages
const HUB_LONG_POLL_TIMEOUT_MS = 120_000;
const app = express();
const PORT = process.env.PROXY_PORT || 80;
const API_BASE_URL = normalizeProxyBaseUrl(
  process.env.CONTROL_PLANE_API_BASE_URL,
);

function normalizeProxyBaseUrl(
  baseUrl: string | undefined,
): string | undefined {
  if (!baseUrl || baseUrl.trim().length === 0) {
    return undefined;
  }

  const withoutTrailingSlash = baseUrl.trim().replace(/\/+$/, "");
  const lower = withoutTrailingSlash.toLowerCase();

  if (lower.endsWith("/api/v1/drasi")) {
    return withoutTrailingSlash.slice(0, -"/v1/drasi".length);
  }

  if (lower.endsWith("/api/drasi")) {
    return withoutTrailingSlash.slice(0, -"/drasi".length);
  }

  if (lower.endsWith("/api/v1")) {
    return withoutTrailingSlash.slice(0, -"/v1".length);
  }

  return withoutTrailingSlash;
}

// In production Docker, server.js runs from dist-server/, so SPA files are in ../dist
// In development, adjust path as needed
const distPath =
  process.env.NODE_ENV === "production"
    ? path.resolve("/app/dist")
    : path.resolve(process.cwd(), "dist");

// Middleware: parse JSON request bodies
app.use(express.json());

/**
 * Proxy middleware: forward /hubs/* requests to Control Plane API SignalR hubs.
 * Placed AFTER express.json() — for /hubs/ we collect raw body from req.body
 * or the stream, since SignalR negotiate sends empty or text/plain bodies.
 *
 * SSE transport streams responses; negotiate and long-polling buffer-and-forward.
 */
app.use("/hubs/", async (req: Request, res: Response) => {
  try {
    if (!API_BASE_URL) {
      return res.status(500).json({
        title: "Configuration Error",
        status: 500,
        detail: "CONTROL_PLANE_API_BASE_URL not configured",
        errorCode: "ConfigurationMissing",
      });
    }

    // Backend hubs are at root path, not under /api
    const backendRoot = API_BASE_URL.replace(/\/api$/i, "");
    const targetUrl = `${backendRoot}/hubs${req.url}`;

    const headers: Record<string, string> = {};
    Object.entries(req.headers).forEach(([key, value]) => {
      if (
        !["host", "connection", "content-length"].includes(key.toLowerCase())
      ) {
        headers[key] = value?.toString() || "";
      }
    });

    let body: string | undefined;
    if (req.method !== "GET" && req.method !== "HEAD") {
      body = typeof req.body === "string" ? req.body : JSON.stringify(req.body);
    }

    const isSSE = req.headers.accept?.includes("text/event-stream");
    const controller = new AbortController();

    if (isSSE) {
      // SSE streams are long-lived — abort only on client disconnect
      req.on("close", () => controller.abort());

      const response = await fetch(targetUrl, {
        method: req.method,
        headers,
        signal: controller.signal,
      });

      res.status(response.status);
      response.headers.forEach((value, key) => {
        if (
          !["content-encoding", "transfer-encoding", "content-length"].includes(
            key.toLowerCase(),
          )
        ) {
          res.setHeader(key, value);
        }
      });
      res.setHeader("Cache-Control", "no-cache");
      res.flushHeaders();

      if (response.body) {
        const reader = response.body.getReader();
        const pump = async () => {
          while (true) {
            const { done, value } = await reader.read();
            if (done) break;
            if (!res.writableEnded) res.write(value);
          }
          if (!res.writableEnded) res.end();
        };
        pump().catch((err) => {
          if (err.name !== "AbortError")
            console.error("Hub SSE proxy error:", err);
          if (!res.writableEnded) res.end();
        });
      } else {
        res.end();
      }
    } else {
      // Negotiate + long polling — use longer timeout than API requests
      const timeout = setTimeout(
        () => controller.abort(),
        HUB_LONG_POLL_TIMEOUT_MS,
      );

      const response = await fetch(targetUrl, {
        method: req.method,
        headers,
        signal: controller.signal,
        ...(body && { body }),
      });

      clearTimeout(timeout);

      response.headers.forEach((value, key) => {
        if (
          !["content-encoding", "transfer-encoding", "content-length"].includes(
            key.toLowerCase(),
          )
        ) {
          res.setHeader(key, value);
        }
      });

      const responseBody = await response.text();
      res.status(response.status);
      res.send(responseBody);
    }
  } catch (error) {
    if ((error as Error).name === "AbortError") {
      if (!res.headersSent) {
        return res.status(504).json({
          title: "Gateway Timeout",
          status: 504,
          detail: "Hub proxy request timed out",
          errorCode: "ProxyTimeout",
        });
      }
      return;
    }
    console.error("Hub proxy error:", error);
    if (!res.headersSent) {
      res.status(500).json({
        title: "Proxy Error",
        status: 500,
        detail: error instanceof Error ? error.message : String(error),
        errorCode: "ProxyError",
      });
    }
  }
});

/**
 * Proxy middleware: forward /api/v1/* requests to Control Plane API
 * In dev mode with ATLAS_ALLOW_ANONYMOUS=true, authentication is not required
 */
app.use("/api/v1/", async (req: Request, res: Response) => {
  try {
    if (!API_BASE_URL) {
      return res.status(500).json({
        title: "Configuration Error",
        status: 500,
        detail: "CONTROL_PLANE_API_BASE_URL not configured",
        errorCode: "ConfigurationMissing",
      });
    }

    // Build target URL
    const targetUrl = `${API_BASE_URL}/v1${req.url}`;

    // Prepare request body
    let body: string | undefined;
    if (req.method !== "GET" && req.method !== "HEAD") {
      body = typeof req.body === "string" ? req.body : JSON.stringify(req.body);
    }

    // Prepare request headers (forward from browser request)
    // Strip host, connection, and content-length — content-length is recalculated
    // by fetch() based on the actual body bytes we send (which may differ from the
    // browser's original body if express.json() deserialized an empty payload to {}).
    const headers: Record<string, string> = {};
    Object.entries(req.headers).forEach(([key, value]) => {
      if (
        !["host", "connection", "content-length"].includes(key.toLowerCase())
      ) {
        headers[key] = value?.toString() || "";
      }
    });

    // Forward the request without authentication
    // In dev mode, API allows anonymous read via ATLAS_ALLOW_ANONYMOUS=true
    const controller = new AbortController();
    const timeout = setTimeout(() => controller.abort(), PROXY_TIMEOUT_MS);

    const response = await fetch(targetUrl, {
      method: req.method,
      headers,
      signal: controller.signal,
      ...(body && { body }),
    });

    clearTimeout(timeout);

    // Forward response headers.
    // Strip content-encoding (fetch() already decoded the body), transfer-encoding,
    // and content-length (Express recalculates it from the actual decoded body bytes;
    // keeping the original compressed content-length causes a length mismatch that
    // makes the Container Apps Envoy ingress tear the stream with "stream timeout").
    response.headers.forEach((value, key) => {
      if (
        !["content-encoding", "transfer-encoding", "content-length"].includes(
          key.toLowerCase(),
        )
      ) {
        res.setHeader(key, value);
      }
    });

    const responseBody = await response.text();
    res.status(response.status);
    res.send(responseBody);
  } catch (error) {
    console.error("Proxy error:", error);
    res.status(500).json({
      title: "Proxy Error",
      status: 500,
      detail: error instanceof Error ? error.message : String(error),
      errorCode: "ProxyError",
    });
  }
});

// Serve static SPA files from dist/
app.use(express.static(distPath));

// Health check endpoints
app.get("/health/live", (req, res) => {
  res.json({ status: "alive" });
});

app.get("/health/ready", (req, res) => {
  if (!API_BASE_URL) {
    return res
      .status(503)
      .json({
        status: "not ready",
        reason: "CONTROL_PLANE_API_BASE_URL not configured",
      });
  }
  res.json({ status: "ready" });
});

// SPA fallback: serve index.html for all non-API routes (for client-side routing).
// Use app.use() for Express 5 compatibility (path-to-regexp rejects bare '*').
app.use((req, res) => {
  res.sendFile(path.join(distPath, "index.html"));
});

app.listen(PORT, () => {
  console.log(`Frontend proxy server running on port ${PORT}`);
  console.log(`API proxy: ${API_BASE_URL ? "Configured" : "NOT CONFIGURED"}`);
  console.log(
    `Hub proxy: ${API_BASE_URL ? API_BASE_URL.replace(/\/api$/i, "") + "/hubs/*" : "NOT CONFIGURED"}`,
  );
  console.log(`Static files: ${distPath}`);

  // Test API connectivity on startup (log-only, don't block startup)
  if (API_BASE_URL) {
    const backendRoot = API_BASE_URL.replace(/\/api$/i, "");
    fetch(`${backendRoot}/health/ready`, {
      signal: AbortSignal.timeout(5_000),
    })
      .then((r) => console.log(`✓ API connectivity check: ${r.status}`))
      .catch((e) =>
        console.error(`✗ API connectivity check failed: ${e.message}`),
      );
  }
});
