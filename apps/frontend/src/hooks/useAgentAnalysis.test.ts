import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { useAgentAnalysis } from "./useAgentAnalysis";

function createSseResponse(events: unknown[]): Response {
  const encoder = new TextEncoder();
  const payload = events
    .map((event) => `data: ${JSON.stringify(event)}\n\n`)
    .join("");

  const stream = new ReadableStream<Uint8Array>({
    start(controller) {
      controller.enqueue(encoder.encode(payload));
      controller.close();
    },
  });

  return new Response(stream, {
    status: 200,
    headers: { "Content-Type": "text/event-stream" },
  });
}

describe("useAgentAnalysis", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it("tracks agent execution and summary from analysis stream", async () => {
    vi.spyOn(globalThis, "fetch").mockResolvedValue(
      createSseResponse([
        {
          type: "RUN_STARTED",
          runId: "run-42",
          serviceGroupName: "Payments",
        },
        {
          type: "TOOL_CALL_START",
          toolCallId: "tool-1",
          toolCallName: "assessDrift",
          agentName: "DriftDetectionAgent",
        },
        {
          type: "TOOL_CALL_ARGS",
          toolCallId: "tool-1",
          delta: '{"scope":"subscriptions"}',
        },
        {
          type: "TOOL_CALL_END",
          toolCallId: "tool-1",
          output: "ok",
          elapsedMs: 120,
        },
        { type: "TEXT_MESSAGE_START", messageId: "summary-1" },
        {
          type: "TEXT_MESSAGE_CONTENT",
          messageId: "summary-1",
          delta: "All checks passed.",
        },
        { type: "TEXT_MESSAGE_END", messageId: "summary-1" },
        {
          type: "STATE_SNAPSHOT",
          snapshot: { analyzedResources: 15 },
        },
        { type: "RUN_FINISHED", runId: "run-42" },
      ]),
    );

    const { result } = renderHook(() => useAgentAnalysis());

    await act(async () => {
      await result.current.startAnalysis("sg-1", {
        baseUrl: "https://example.test",
      });
    });

    await waitFor(() => {
      expect(result.current.state.runId).toBe("run-42");
      expect(result.current.state.serviceGroupName).toBe("Payments");
      expect(result.current.state.streaming).toBe(false);
      expect(result.current.state.summary).toContain("All checks passed.");
      expect(result.current.state.snapshot).toMatchObject({
        analyzedResources: 15,
      });
      expect(result.current.state.agents[0]).toMatchObject({
        id: "tool-1",
        agentName: "DriftDetectionAgent",
        toolName: "assessDrift",
        status: "completed",
      });
    });
  });
});
