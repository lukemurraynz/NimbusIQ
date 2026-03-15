/**
 * AG-UI Protocol hook for streaming multi-agent analysis runs.
 *
 * Consumes POST /api/v1/agents/analysis-stream/{serviceGroupId} and exposes
 * each NimbusIQ agent (DriftDetection, BestPractice, WellArchitected, FinOps,
 * ServiceHierarchy) as a real-time AG-UI TOOL_CALL_* sequence — making the
 * Microsoft Agent Framework orchestration fully transparent in the UI.
 *
 * This demonstrates that AG-UI is not limited to chat: any multi-agent
 * workflow that produces TOOL_CALL_* events can be visualised with this hook.
 *
 * AG-UI protocol reference: https://github.com/ag-ui-protocol/ag-ui
 */

import { useCallback, useRef, useState } from "react";
import type {
  BaseEvent,
  RunStartedEvent,
  RunFinishedEvent,
  RunErrorEvent,
  ToolCallStartEvent,
  ToolCallArgsEvent,
  ToolCallEndEvent,
  TextMessageStartEvent,
  TextMessageContentEvent,
  TextMessageEndEvent,
  StateSnapshotEvent,
} from "@ag-ui/core";

// ─── Public types ─────────────────────────────────────────────────────────────

export type AgentStatus = "pending" | "running" | "completed" | "failed";

export interface AgentExecution {
  /** AG-UI toolCallId */
  id: string;
  /** NimbusIQ agent name e.g. "DriftDetectionAgent" */
  agentName: string;
  /** Tool function name e.g. "assessDrift" */
  toolName: string;
  /** Streamed JSON args */
  args: string;
  /** JSON result from TOOL_CALL_END.output */
  output?: string;
  /** Server-side elapsed time in ms */
  elapsedMs?: number;
  status: AgentStatus;
}

export interface AnalysisRunState {
  /** AG-UI runId from RUN_STARTED */
  runId: string | null;
  serviceGroupId: string | null;
  serviceGroupName: string | null;
  /** Ordered list of agents executed (same order as server emits) */
  agents: AgentExecution[];
  /** Streamed natural-language summary text */
  summary: string;
  summarising: boolean;
  /** Final STATE_SNAPSHOT payload */
  snapshot: Record<string, unknown> | null;
  streaming: boolean;
  error: string | null;
}

export interface StartAnalysisOptions {
  baseUrl?: string;
  accessToken?: string;
}

// ─── Hook ─────────────────────────────────────────────────────────────────────

const DEFAULT_BASE_URL =
  (import.meta.env["VITE_CONTROL_PLANE_API_URL"] as string | undefined) ?? "";

/**
 * Streams an AG-UI analysis run for the given service group.
 * Exposes each NimbusIQ agent execution as a typed `AgentExecution` so the UI
 * can render a transparent multi-agent orchestration timeline.
 */
export function useAgentAnalysis() {
  const abortRef = useRef<AbortController | null>(null);
  const summaryMsgRef = useRef<string | null>(null);

  const [state, setState] = useState<AnalysisRunState>({
    runId: null,
    serviceGroupId: null,
    serviceGroupName: null,
    agents: [],
    summary: "",
    summarising: false,
    snapshot: null,
    streaming: false,
    error: null,
  });

  const startAnalysis = useCallback(
    async (serviceGroupId: string, opts: StartAnalysisOptions = {}) => {
      // Cancel any in-flight request
      abortRef.current?.abort();
      abortRef.current = new AbortController();
      const signal = abortRef.current.signal;

      setState({
        runId: null,
        serviceGroupId,
        serviceGroupName: null,
        agents: [],
        summary: "",
        summarising: false,
        snapshot: null,
        streaming: true,
        error: null,
      });

      const baseUrl = opts.baseUrl ?? DEFAULT_BASE_URL;
      const url = `${baseUrl}/api/v1/agents/analysis-stream/${serviceGroupId}`;

      try {
        const response = await fetch(url, {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
            ...(opts.accessToken
              ? { Authorization: `Bearer ${opts.accessToken}` }
              : {}),
          },
          signal,
        });

        if (!response.ok) {
          const text = await response.text();
          throw new Error(`HTTP ${response.status}: ${text}`);
        }

        if (!response.body) throw new Error("No response body");

        const reader = response.body.getReader();
        const decoder = new TextDecoder();
        let buffer = "";

        // ─── SSE parser ────────────────────────────────────────────────────────
        while (true) {
          const { done, value } = await reader.read();
          if (done) break;

          buffer += decoder.decode(value, { stream: true });

          const blocks = buffer.split("\n\n");
          buffer = blocks.pop() ?? "";

          for (const block of blocks) {
            const dataLine = block
              .split("\n")
              .find((l) => l.startsWith("data:"));
            if (!dataLine) continue;

            const json = dataLine.slice(5).trim();
            if (!json) continue;

            let event: BaseEvent;
            try {
              event = JSON.parse(json) as BaseEvent;
            } catch (parseError) {
              if (import.meta.env.DEV) {
                console.debug(
                  "Skipping malformed analysis-stream SSE event",
                  parseError,
                );
              }
              continue;
            }

            dispatchEvent(event);
          }
        }

        // Flush remaining buffer
        if (buffer.trim()) {
          const dataLine = buffer
            .split("\n")
            .find((l) => l.startsWith("data:"));
          if (dataLine) {
            const json = dataLine.slice(5).trim();
            if (json) {
              try {
                dispatchEvent(JSON.parse(json) as BaseEvent);
              } catch (parseError) {
                if (import.meta.env.DEV) {
                  console.debug(
                    "Skipping malformed trailing analysis-stream SSE event",
                    parseError,
                  );
                }
              }
            }
          }
        }

        // ─── Inner dispatcher ──────────────────────────────────────────────────
        function dispatchEvent(event: BaseEvent) {
          switch (event.type) {
            case "RUN_STARTED": {
              const e = event as RunStartedEvent & {
                serviceGroupId?: string;
                serviceGroupName?: string;
              };
              setState((prev) => ({
                ...prev,
                runId: e.runId,
                serviceGroupName: e.serviceGroupName ?? null,
              }));
              break;
            }

            case "RUN_FINISHED": {
              const _e = event as RunFinishedEvent;
              void _e;
              setState((prev) => ({
                ...prev,
                streaming: false,
                summarising: false,
                agents: prev.agents.map((a) =>
                  a.status === "running"
                    ? { ...a, status: "completed" as AgentStatus }
                    : a,
                ),
              }));
              break;
            }

            case "RUN_ERROR": {
              const e = event as RunErrorEvent;
              setState((prev) => ({
                ...prev,
                streaming: false,
                summarising: false,
                error: e.message ?? "Analysis failed",
                agents: prev.agents.map((a) =>
                  a.status === "running"
                    ? { ...a, status: "failed" as AgentStatus }
                    : a,
                ),
              }));
              break;
            }

            case "TOOL_CALL_START": {
              const e = event as ToolCallStartEvent & { agentName?: string };
              const agentName = e.agentName ?? e.toolCallName;
              const toolName = e.toolCallName;
              setState((prev) => ({
                ...prev,
                agents: [
                  ...prev.agents,
                  {
                    id: e.toolCallId,
                    agentName,
                    toolName,
                    args: "",
                    status: "running" as AgentStatus,
                  },
                ],
              }));
              break;
            }

            case "TOOL_CALL_ARGS": {
              const e = event as ToolCallArgsEvent;
              setState((prev) => ({
                ...prev,
                agents: prev.agents.map((a) =>
                  a.id === e.toolCallId
                    ? { ...a, args: a.args + (e.delta ?? "") }
                    : a,
                ),
              }));
              break;
            }

            case "TOOL_CALL_END": {
              const e = event as ToolCallEndEvent & { elapsedMs?: number };
              setState((prev) => ({
                ...prev,
                agents: prev.agents.map((a) =>
                  a.id === e.toolCallId
                    ? {
                        ...a,
                        status: "completed" as AgentStatus,
                        output: (e as ToolCallEndEvent & { output?: string })
                          .output,
                        elapsedMs: e.elapsedMs,
                      }
                    : a,
                ),
              }));
              break;
            }

            case "TEXT_MESSAGE_START": {
              const e = event as TextMessageStartEvent;
              summaryMsgRef.current = e.messageId;
              setState((prev) => ({ ...prev, summarising: true }));
              break;
            }

            case "TEXT_MESSAGE_CONTENT": {
              const e = event as TextMessageContentEvent;
              if (summaryMsgRef.current === e.messageId) {
                setState((prev) => ({
                  ...prev,
                  summary: prev.summary + (e.delta ?? ""),
                }));
              }
              break;
            }

            case "TEXT_MESSAGE_END": {
              const e = event as TextMessageEndEvent;
              if (summaryMsgRef.current === e.messageId) {
                summaryMsgRef.current = null;
                setState((prev) => ({ ...prev, summarising: false }));
              }
              break;
            }

            case "STATE_SNAPSHOT": {
              const e = event as StateSnapshotEvent;
              setState((prev) => ({
                ...prev,
                snapshot: e.snapshot as Record<string, unknown>,
              }));
              break;
            }

            default:
              break;
          }
        }
      } catch (err) {
        if (err instanceof DOMException && err.name === "AbortError") return;
        const msg = err instanceof Error ? err.message : String(err);
        setState((prev) => ({ ...prev, streaming: false, error: msg }));
      }
    },
    [],
  );

  const cancel = useCallback(() => {
    abortRef.current?.abort();
  }, []);

  const reset = useCallback(() => {
    abortRef.current?.abort();
    summaryMsgRef.current = null;
    setState({
      runId: null,
      serviceGroupId: null,
      serviceGroupName: null,
      agents: [],
      summary: "",
      summarising: false,
      snapshot: null,
      streaming: false,
      error: null,
    });
  }, []);

  return { state, startAnalysis, cancel, reset };
}
