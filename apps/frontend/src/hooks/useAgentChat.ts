/**
 * AG-UI Protocol client hook for the NimbusIQ agent chat endpoint.
 *
 * Implements the Agent-User Interaction (AG-UI) protocol by consuming the
 * SSE stream produced by POST /api/v1/chat/agent.  The hook parses typed
 * AG-UI events (RUN_*, TEXT_MESSAGE_*, TOOL_CALL_*, STATE_SNAPSHOT) and
 * exposes clean React state for the chat panel to render.
 *
 * AG-UI protocol reference: https://github.com/ag-ui-protocol/ag-ui
 */

import { useCallback, useEffect, useRef, useState } from "react";
import type {
  BaseEvent,
  RunStartedEvent,
  RunFinishedEvent,
  RunErrorEvent,
  TextMessageStartEvent,
  TextMessageContentEvent,
  TextMessageEndEvent,
  ToolCallStartEvent,
  ToolCallArgsEvent,
  ToolCallEndEvent,
  StateSnapshotEvent,
} from "@ag-ui/core";

// ─── Public types ─────────────────────────────────────────────────────────────

export type ChatRole = "user" | "assistant";

export interface ChatMessage {
  id: string;
  role: ChatRole;
  content: string;
  /** True while the assistant is still streaming content. */
  streaming?: boolean;
}

export interface ToolCall {
  id: string;
  name: string;
  args: string;
  output?: string;
  /** True while the tool is being called. */
  running: boolean;
}

export interface InfraStateSnapshot {
  serviceGroupCount?: number;
  serviceGroupNames?: string[];
  serviceGroupIds?: string[];
  recentRunStatuses?: string[];
  recentRunIds?: string[];
  findingCount?: number;
  topRecommendation?: {
    id: string;
    serviceGroupId?: string;
    title?: string;
    category?: string;
    priority?: string;
    status?: string;
  } | null;
  latestDrift?: {
    id: string;
    serviceGroupId?: string;
    driftScore?: number;
    totalViolations?: number;
    snapshotTime?: string;
  } | null;
  governanceConflict?: {
    id: string;
    firstTitle?: string;
    secondId?: string;
    secondTitle?: string;
    resourceId?: string;
    serviceGroupId?: string;
  } | null;
  capabilityModes?: {
    chat?: string;
    remediation?: string;
    drift?: string;
  };
}

export interface AgentChatState {
  messages: ChatMessage[];
  toolCalls: ToolCall[];
  infraState: InfraStateSnapshot;
  running: boolean;
  error: string | null;
}

export interface UseAgentChatResult {
  state: AgentChatState;
  sendMessage: (
    userContent: string,
    opts?: SendMessageOptions,
  ) => Promise<void>;
  retryLast: (opts?: SendMessageOptions) => Promise<void>;
  canRetryLast: boolean;
  clearError: () => void;
  reset: () => void;
  restoredFromSession: boolean;
}

export interface SendMessageOptions {
  baseUrl?: string;
  accessToken?: string;
}

// ─── Hook ─────────────────────────────────────────────────────────────────────

const DEFAULT_BASE_URL =
  (import.meta.env["VITE_CONTROL_PLANE_API_URL"] as string | undefined) ?? "";
const SESSION_STORAGE_KEY = "nimbusiq.agentChat.v1";

interface PersistedAgentChatState {
  threadId: string;
  messages: ChatMessage[];
  toolCalls: ToolCall[];
  infraState: InfraStateSnapshot;
}

function mapAgentError(message: string): string {
  if (message.includes("HTTP 401") || message.includes("HTTP 403")) {
    return "Your session is not authorized to use the assistant. Re-authenticate and try again.";
  }
  if (message.includes("HTTP 429")) {
    return "The assistant is rate limited right now. Wait a moment and retry.";
  }
  if (message.includes("HTTP 5")) {
    return "The assistant service is temporarily unavailable. Please retry.";
  }
  if (message.includes("Failed to fetch") || message.includes("NetworkError")) {
    return "Network connection failed while contacting the assistant. Check connectivity and retry.";
  }

  return message;
}

function loadPersistedState(): PersistedAgentChatState | null {
  try {
    const raw = sessionStorage.getItem(SESSION_STORAGE_KEY);
    if (!raw) return null;
    const parsed = JSON.parse(raw) as Partial<PersistedAgentChatState>;
    if (!parsed.threadId || !Array.isArray(parsed.messages)) return null;

    return {
      threadId: parsed.threadId,
      messages: parsed.messages,
      toolCalls: Array.isArray(parsed.toolCalls) ? parsed.toolCalls : [],
      infraState: parsed.infraState ?? {},
    };
  } catch {
    return null;
  }
}

/**
 * Manages AG-UI chat state and provides `sendMessage` to stream a new turn.
 */
export function useAgentChat(): UseAgentChatResult {
  const persisted = loadPersistedState();
  const threadIdRef = useRef<string>(
    persisted?.threadId ?? crypto.randomUUID(),
  );
  const abortRef = useRef<AbortController | null>(null);
  const lastUserMessageRef = useRef<string>(
    persisted?.messages.filter((m) => m.role === "user").at(-1)?.content ?? "",
  );
  const restoredFromSession = Boolean(persisted?.messages.length);

  const [state, setState] = useState<AgentChatState>({
    messages: persisted?.messages ?? [],
    toolCalls: persisted?.toolCalls ?? [],
    infraState: persisted?.infraState ?? {},
    running: false,
    error: null,
  });

  useEffect(() => {
    try {
      const snapshot: PersistedAgentChatState = {
        threadId: threadIdRef.current,
        messages: state.messages,
        toolCalls: state.toolCalls,
        infraState: state.infraState,
      };
      sessionStorage.setItem(SESSION_STORAGE_KEY, JSON.stringify(snapshot));
    } catch (error) {
      if (import.meta.env.DEV) {
        console.debug("Unable to persist agent chat session state", error);
      }
    }
  }, [state.messages, state.toolCalls, state.infraState]);

  const sendMessage = useCallback(
    async (userContent: string, opts: SendMessageOptions = {}) => {
      if (!userContent.trim()) return;
      lastUserMessageRef.current = userContent.trim();

      // Cancel any in-flight request
      abortRef.current?.abort();
      abortRef.current = new AbortController();
      const signal = abortRef.current.signal;

      const userMsg: ChatMessage = {
        id: crypto.randomUUID(),
        role: "user",
        content: userContent.trim(),
      };

      // Append the user message immediately so the UI feels responsive
      setState((prev) => ({
        ...prev,
        messages: [...prev.messages, userMsg],
        running: true,
        error: null,
      }));

      const baseUrl = opts.baseUrl ?? DEFAULT_BASE_URL;
      const url = `${baseUrl}/api/v1/chat/agent`;

      const allMessages = await new Promise<ChatMessage[]>((resolve) => {
        setState((prev) => {
          resolve([...prev.messages]);
          return prev;
        });
      });

      try {
        const response = await fetch(url, {
          method: "POST",
          headers: {
            "Content-Type": "application/json",
            ...(opts.accessToken
              ? { Authorization: `Bearer ${opts.accessToken}` }
              : {}),
          },
          body: JSON.stringify({
            threadId: threadIdRef.current,
            runId: crypto.randomUUID(),
            messages: allMessages.map((m) => ({
              id: m.id,
              role: m.role,
              content: m.content,
            })),
          }),
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

        // Track the active streaming assistant message ID
        let streamingMsgId: string | null = null;

        while (true) {
          const { done, value } = await reader.read();
          if (done) break;

          buffer += decoder.decode(value, { stream: true });

          // SSE events are separated by double newlines
          const lines = buffer.split("\n\n");
          buffer = lines.pop() ?? "";

          for (const block of lines) {
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
                console.debug("Skipping malformed AG-UI SSE event", parseError);
              }
              continue;
            }

            dispatchEvent(event);
          }
        }

        // Flush any remaining buffer
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
                    "Skipping malformed trailing AG-UI SSE event",
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
              // RunStartedEvent — we already set running=true above
              const _e = event as RunStartedEvent;
              void _e; // consumed
              break;
            }

            case "RUN_FINISHED": {
              const _e = event as RunFinishedEvent;
              void _e;
              setState((prev) => ({
                ...prev,
                running: false,
                // Mark any still-streaming message as complete
                messages: prev.messages.map((m) =>
                  m.streaming ? { ...m, streaming: false } : m,
                ),
              }));
              break;
            }

            case "RUN_ERROR": {
              const e = event as RunErrorEvent;
              setState((prev) => ({
                ...prev,
                running: false,
                error: e.message ?? "Unknown error from agent",
                messages: prev.messages.map((m) =>
                  m.streaming ? { ...m, streaming: false } : m,
                ),
              }));
              break;
            }

            case "TEXT_MESSAGE_START": {
              const e = event as TextMessageStartEvent;
              streamingMsgId = e.messageId;
              const newMsg: ChatMessage = {
                id: e.messageId,
                role: "assistant",
                content: "",
                streaming: true,
              };
              setState((prev) => ({
                ...prev,
                messages: [...prev.messages, newMsg],
              }));
              break;
            }

            case "TEXT_MESSAGE_CONTENT": {
              const e = event as TextMessageContentEvent;
              setState((prev) => ({
                ...prev,
                messages: prev.messages.map((m) =>
                  m.id === e.messageId
                    ? { ...m, content: m.content + (e.delta ?? "") }
                    : m,
                ),
              }));
              break;
            }

            case "TEXT_MESSAGE_END": {
              const e = event as TextMessageEndEvent;
              setState((prev) => ({
                ...prev,
                messages: prev.messages.map((m) =>
                  m.id === e.messageId ? { ...m, streaming: false } : m,
                ),
              }));
              if (streamingMsgId === e.messageId) streamingMsgId = null;
              break;
            }

            case "TOOL_CALL_START": {
              const e = event as ToolCallStartEvent;
              setState((prev) => ({
                ...prev,
                toolCalls: [
                  ...prev.toolCalls,
                  {
                    id: e.toolCallId,
                    name: e.toolCallName,
                    args: "",
                    running: true,
                  },
                ],
              }));
              break;
            }

            case "TOOL_CALL_ARGS": {
              const e = event as ToolCallArgsEvent;
              setState((prev) => ({
                ...prev,
                toolCalls: prev.toolCalls.map((tc) =>
                  tc.id === e.toolCallId
                    ? { ...tc, args: tc.args + (e.delta ?? "") }
                    : tc,
                ),
              }));
              break;
            }

            case "TOOL_CALL_END": {
              const e = event as ToolCallEndEvent;
              setState((prev) => ({
                ...prev,
                toolCalls: prev.toolCalls.map((tc) =>
                  tc.id === e.toolCallId
                    ? {
                        ...tc,
                        running: false,
                        output: (
                          event as ToolCallEndEvent & { output?: string }
                        ).output,
                      }
                    : tc,
                ),
              }));
              break;
            }

            case "STATE_SNAPSHOT": {
              const e = event as StateSnapshotEvent;
              setState((prev) => ({
                ...prev,
                infraState: e.snapshot as InfraStateSnapshot,
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
        setState((prev) => ({ ...prev, running: false, error: msg }));
      }
    },
    [],
  );

  const retryLast = useCallback(
    async (opts: SendMessageOptions = {}) => {
      if (!lastUserMessageRef.current.trim() || state.running) return;
      await sendMessage(lastUserMessageRef.current, opts);
    },
    [sendMessage, state.running],
  );

  const clearError = useCallback(() => {
    setState((prev) => ({ ...prev, error: null }));
  }, []);

  const reset = useCallback(() => {
    abortRef.current?.abort();
    threadIdRef.current = crypto.randomUUID();
    lastUserMessageRef.current = "";
    try {
      sessionStorage.removeItem(SESSION_STORAGE_KEY);
    } catch (error) {
      if (import.meta.env.DEV) {
        console.debug("Unable to clear agent chat session state", error);
      }
    }
    setState({
      messages: [],
      toolCalls: [],
      infraState: {},
      running: false,
      error: null,
    });
  }, []);

  return {
    state: {
      ...state,
      error: state.error ? mapAgentError(state.error) : null,
    },
    sendMessage,
    retryLast,
    canRetryLast: Boolean(lastUserMessageRef.current.trim()) && !state.running,
    clearError,
    reset,
    restoredFromSession,
  };
}
