import { act, renderHook } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { useAgentChat } from "./useAgentChat";

describe("useAgentChat", () => {
  beforeEach(() => {
    sessionStorage.clear();
    vi.restoreAllMocks();
  });

  it("ignores blank user messages and does not start a request", async () => {
    const fetchMock = vi.spyOn(globalThis, "fetch");
    const { result } = renderHook(() => useAgentChat());

    await act(async () => {
      await result.current.sendMessage("   ", {
        baseUrl: "https://example.test",
      });
    });

    expect(fetchMock).not.toHaveBeenCalled();
    expect(result.current.state.messages).toHaveLength(0);
    expect(result.current.state.running).toBe(false);
  });

  it("restores persisted state from session storage", () => {
    sessionStorage.setItem(
      "nimbusiq.agentChat.v1",
      JSON.stringify({
        threadId: "thread-1",
        messages: [{ id: "m1", role: "user", content: "cached" }],
        toolCalls: [],
        infraState: { serviceGroupCount: 1 },
      }),
    );

    const { result } = renderHook(() => useAgentChat());

    expect(result.current.restoredFromSession).toBe(true);
    expect(result.current.state.messages[0]?.content).toBe("cached");
    expect(result.current.state.infraState.serviceGroupCount).toBe(1);
  });
});
