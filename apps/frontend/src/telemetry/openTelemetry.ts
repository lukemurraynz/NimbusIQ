import { WebTracerProvider } from "@opentelemetry/sdk-trace-web";
import { OTLPTraceExporter } from "@opentelemetry/exporter-trace-otlp-http";
import { BatchSpanProcessor } from "@opentelemetry/sdk-trace-web";
import { registerInstrumentations } from "@opentelemetry/instrumentation";
import { FetchInstrumentation } from "@opentelemetry/instrumentation-fetch";

/**
 * Initializes OpenTelemetry for the frontend application with OTLP HTTP export.
 * Automatically instruments fetch() calls and adds correlation IDs.
 */
export function initializeOpenTelemetry(otlpEndpoint?: string): void {
  // Skip if already initialized
  if ((window as unknown as Record<string, boolean>).__OTEL_INITIALIZED__) {
    return;
  }

  (window as unknown as Record<string, boolean>).__OTEL_INITIALIZED__ = true;

  // Skip initialization if no OTLP endpoint configured
  if (!otlpEndpoint) {
    if (import.meta.env.DEV) {
      console.info(
        "[OpenTelemetry] Skipped: No OTLP endpoint configured. Set VITE_OTLP_ENDPOINT to enable.",
      );
    }
    return;
  }

  try {
    // Create OTLP exporter
    const exporter = new OTLPTraceExporter({
      url: otlpEndpoint,
      headers: {
        // Add any required authentication headers here if needed
      },
    });

    // Create tracer provider with batch span processor
    const provider = new WebTracerProvider({
      spanProcessors: [
        new BatchSpanProcessor(exporter, {
          maxQueueSize: 100,
          maxExportBatchSize: 10,
          scheduledDelayMillis: 5000,
        }),
      ],
    });

    provider.register();

    // Register fetch instrumentation to automatically trace API calls
    registerInstrumentations({
      instrumentations: [
        new FetchInstrumentation({
          // Add correlation ID to all fetch requests
          applyCustomAttributesOnSpan: (span, request) => {
            if (request instanceof Request) {
              // Match the header name used in controlPlaneApi.ts
              const correlationId = request.headers.get("X-Correlation-Id");
              if (correlationId) {
                span.setAttribute("correlation.id", correlationId);
              }
            }
          },
          // Propagate trace context headers
          propagateTraceHeaderCorsUrls: [
            new RegExp(import.meta.env.VITE_API_URL || window.location.origin),
          ],
        }),
      ],
    });

    if (import.meta.env.DEV) {
      console.info(
        "[OpenTelemetry] Initialized successfully with OTLP endpoint:",
        otlpEndpoint,
      );
    }
  } catch (error) {
    console.error("[OpenTelemetry] Initialization failed:", error);
  }
}

/**
 * Creates a correlation ID for API requests.
 * Uses crypto.randomUUID() for spec-compliant UUIDs.
 */
export function generateCorrelationId(): string {
  return crypto.randomUUID();
}
