import { act, renderHook, waitFor } from "@testing-library/react";
import { beforeEach, describe, expect, it, vi } from "vitest";
import { useAnalysisPolling } from "./useAnalysisPolling";
import { controlPlaneApi } from "../services/controlPlaneApi";

describe("useAnalysisPolling", () => {
  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it("sets normalized analysis score when run completes", async () => {
    vi.spyOn(controlPlaneApi, "startAnalysisWithMetadata").mockResolvedValue({
      data: { runId: "run-1" },
      metadata: {},
    } as Awaited<ReturnType<typeof controlPlaneApi.startAnalysisWithMetadata>>);

    vi.spyOn(
      controlPlaneApi,
      "getAnalysisStatusWithMetadata",
    ).mockResolvedValue({
      data: { status: "completed" },
      metadata: {},
    } as Awaited<
      ReturnType<typeof controlPlaneApi.getAnalysisStatusWithMetadata>
    >);

    vi.spyOn(
      controlPlaneApi,
      "getAnalysisScoresWithMetadata",
    ).mockResolvedValue({
      data: {
        runId: "run-1",
        serviceGroupId: "sg-1",
        status: "completed",
        scores: [
          {
            category: "Architecture",
            score: 80,
            confidence: 0.9,
            dimensions: { completeness: 90 },
            resourceCount: 5,
            createdAt: new Date().toISOString(),
          },
        ],
      },
      metadata: {},
    } as Awaited<
      ReturnType<typeof controlPlaneApi.getAnalysisScoresWithMetadata>
    >);

    const { result } = renderHook(() => useAnalysisPolling());

    await act(async () => {
      await result.current.startAnalysis("sg-1");
    });

    await waitFor(() => {
      expect(result.current.analysisScore).not.toBeNull();
      expect(result.current.analysisScore?.value).toBeCloseTo(0.8);
      expect(result.current.analysisScore?.dimensions.completeness).toBeCloseTo(
        0.9,
      );
      expect(result.current.completedRunId).toBe("run-1");
    });
  });
});
