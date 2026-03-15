import { useCallback, useEffect, useRef, useState } from "react";
import {
  HubConnectionBuilder,
  HubConnection,
  HttpTransportType,
  LogLevel,
} from "@microsoft/signalr";
import { log } from "../telemetry/logger";

const CONTROL_PLANE_BASE_URL =
  (import.meta.env["VITE_CONTROL_PLANE_API_URL"] as string | undefined) ??
  (import.meta.env["VITE_CONTROL_PLANE_API_BASE_URL"] as string | undefined) ??
  "";

function buildAnalysisHubUrl(): string {
  if (!CONTROL_PLANE_BASE_URL) {
    return "/hubs/analysis";
  }

  const withoutTrailingSlash = CONTROL_PLANE_BASE_URL.replace(/\/$/, "");
  const normalizedBase = withoutTrailingSlash.endsWith("/api/v1")
    ? withoutTrailingSlash.slice(0, -"/api/v1".length)
    : withoutTrailingSlash;

  return `${normalizedBase}/hubs/analysis`;
}

export type AgentStatus = "idle" | "running" | "completed" | "error";

export interface AgentActivity {
  agentName: string;
  status: AgentStatus;
  description: string;
  startedAt: string;
  completedAt?: string;
  elapsedMs?: number;
  itemsProcessed?: number;
  scoreValue?: number;
  summary?: string;
}

export interface AgentFinding {
  agentName: string;
  category: string;
  severity: string;
  message: string;
  detectedAt: string;
}

export interface AgentStreamState {
  agents: AgentActivity[];
  findings: AgentFinding[];
  runCompleted: boolean;
  overallScore?: number;
  connectionError?: string;
}

/**
 * Connects to the /hubs/analysis SignalR hub and streams real-time agent
 * progress events for the given runId.
 *
 * Passes the bearer token via accessTokenFactory so the hub's [Authorize]
 * policy is satisfied. Uses automatic reconnect and stops on unmount.
 */
export function useAgentStream(
  runId: string | null | undefined,
  accessToken?: string,
): AgentStreamState {
  const [state, setState] = useState<AgentStreamState>({
    agents: [],
    findings: [],
    runCompleted: false,
  });
  const connectionRef = useRef<HubConnection | null>(null);

  const upsertAgent = useCallback(
    (update: Partial<AgentActivity> & { agentName: string }) => {
      setState((prev) => {
        const idx = prev.agents.findIndex(
          (a) => a.agentName === update.agentName,
        );
        if (idx < 0) {
          return {
            ...prev,
            agents: [
              ...prev.agents,
              {
                status: "idle",
                description: "",
                startedAt: new Date().toISOString(),
                ...update,
              },
            ],
          };
        }
        const next = [...prev.agents];
        next[idx] = { ...next[idx], ...update };
        return { ...prev, agents: next };
      });
    },
    [],
  );

  useEffect(() => {
    if (!runId) return;

    const connection = new HubConnectionBuilder()
      .withUrl(buildAnalysisHubUrl(), {
        // Restrict to SSE/LongPolling so the token is sent in the Authorization
        // header rather than as ?access_token= in the WebSocket upgrade URL
        // (WebSocket handshake URLs appear in server access logs).
        transport:
          HttpTransportType.ServerSentEvents | HttpTransportType.LongPolling,
        // Pass the bearer token so the [Authorize(Policy = "AnalysisRead")] hub
        // attribute accepts the connection. Falls back to empty string when
        // running in anonymous/demo mode (NIMBUSIQ_ALLOW_ANONYMOUS=true).
        accessTokenFactory: () => accessToken ?? "",
      })
      .withAutomaticReconnect()
      .configureLogging(LogLevel.Warning)
      .build();

    connectionRef.current = connection;

    connection.on(
      "AgentStarted",
      (event: {
        runId: string;
        agentName: string;
        description: string;
        startedAt: string;
      }) => {
        if (event.runId !== runId) return;
        upsertAgent({
          agentName: event.agentName,
          status: "running",
          description: event.description,
          startedAt: event.startedAt,
        });
      },
    );

    connection.on(
      "AgentCompleted",
      (event: {
        runId: string;
        agentName: string;
        success: boolean;
        elapsedMs: number;
        itemsProcessed?: number;
        scoreValue?: number;
        summary?: string;
        completedAt: string;
      }) => {
        if (event.runId !== runId) return;
        upsertAgent({
          agentName: event.agentName,
          status: event.success ? "completed" : "error",
          completedAt: event.completedAt,
          elapsedMs: event.elapsedMs,
          itemsProcessed: event.itemsProcessed ?? undefined,
          scoreValue: event.scoreValue ?? undefined,
          summary: event.summary ?? undefined,
        });
      },
    );

    connection.on(
      "AgentFinding",
      (event: {
        runId: string;
        agentName: string;
        category: string;
        severity: string;
        message: string;
        detectedAt: string;
      }) => {
        if (event.runId !== runId) return;
        setState((prev) => ({
          ...prev,
          findings: [
            ...prev.findings,
            {
              agentName: event.agentName,
              category: event.category,
              severity: event.severity,
              message: event.message,
              detectedAt: event.detectedAt,
            },
          ],
        }));
      },
    );

    connection.on(
      "RunCompleted",
      (event: { runId: string; overallScore: number }) => {
        if (event.runId !== runId) return;
        setState((prev) => ({
          ...prev,
          runCompleted: true,
          overallScore: event.overallScore,
        }));
      },
    );

    connection
      .start()
      .then(() => connection.invoke("SubscribeToRun", runId))
      .catch((err: unknown) => {
        const msg = err instanceof Error ? err.message : String(err);
        setState((prev) => ({ ...prev, connectionError: `SignalR: ${msg}` }));
      });

    return () => {
      connection.stop().catch((err: unknown) => {
        log.error("SignalR cleanup error:", {
          error: err instanceof Error ? err.message : String(err),
        });
      });
    };
  }, [runId, accessToken, upsertAgent]);

  return state;
}
