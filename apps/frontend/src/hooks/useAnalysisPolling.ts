import { useCallback, useRef, useState } from "react";
import { controlPlaneApi } from "../services/controlPlaneApi";
import { log } from "../telemetry/logger";

export interface AnalysisScore {
  value: number;
  level: string;
  confidence?: number;
  degradationFactors: string[];
  dimensions: Record<string, number>;
  resourceCount: number;
}

/**
 * How long (ms) to wait after detecting run completion before clearing
 * `activeRunId`.  This gives the SignalR connection time to receive the
 * `RunCompleted` event before it is torn down, so the AgentActivityPanel
 * overall-score badge is always populated.  If network latency exceeds this
 * window the badge may not be shown — it is intentionally generous to make
 * that case rare.
 */
const SIGNALR_EVENT_BUFFER_MS = 2000;

/**
 * Custom hook to manage analysis polling and result handling.
 * Encapsulates recursive polling logic with setTimeout and status checking.
 */
export function useAnalysisPolling(accessToken?: string): {
  analysisScore: AnalysisScore | null;
  analyzingGroupId: string | null;
  activeRunId: string | null;
  completedRunId: string | null;
  error?: string;
  startAnalysis: (groupId: string) => Promise<void>;
} {
  const [analysisScore, setAnalysisScore] = useState<AnalysisScore | null>(
    null,
  );
  const [analyzingGroupId, setAnalyzingGroupId] = useState<string | null>(null);
  const [activeRunId, setActiveRunId] = useState<string | null>(null);
  const [completedRunId, setCompletedRunId] = useState<string | null>(null);
  const [error, setError] = useState<string | undefined>(undefined);
  const activePollTokenRef = useRef<string | null>(null);

  const clampPollMs = (seconds?: number): number => {
    if (typeof seconds !== "number" || Number.isNaN(seconds)) return 3000;
    const ms = seconds * 1000;
    return Math.max(2000, Math.min(10000, ms));
  };

  const pollAnalysisStatus = useCallback(
    async (
      serviceGroupId: string,
      runId: string,
      correlationId: string,
    ): Promise<void> => {
      return new Promise((resolve) => {
        const token = crypto.randomUUID();
        activePollTokenRef.current = token;

        const checkStatus = async () => {
          if (activePollTokenRef.current !== token) {
            resolve();
            return;
          }

          try {
            const statusResponse =
              await controlPlaneApi.getAnalysisStatusWithMetadata(
                serviceGroupId,
                runId,
                accessToken,
                correlationId,
              );
            const data = statusResponse.data;
            const retryAfterMs = clampPollMs(
              statusResponse.metadata.retryAfterSeconds,
            );

            if (data.status === "completed" || data.status === "partial") {
              try {
                const scoresResponse =
                  await controlPlaneApi.getAnalysisScoresWithMetadata(
                    serviceGroupId,
                    runId,
                    accessToken,
                    correlationId,
                  );
                const scoresData = scoresResponse.data;
                const allScores = scoresData.scores ?? [];
                const avgScore =
                  allScores.length > 0
                    ? allScores.reduce((sum, s) => sum + (s.score ?? 0), 0) /
                      allScores.length /
                      100
                    : 0;
                const mergedDimensions: Record<string, number> = {};
                for (const s of allScores) {
                  if (s.dimensions) {
                    for (const [k, v] of Object.entries(s.dimensions)) {
                      if (v && !(k in mergedDimensions))
                        // API returns dimensions in 0-100 range; normalize to 0.0-1.0
                        mergedDimensions[k] = v / 100;
                    }
                  }
                }
                const totalResources = allScores.reduce(
                  (sum, s) => sum + (s.resourceCount ?? 0),
                  0,
                );
                const level =
                  avgScore >= 0.7 ? "high" : avgScore >= 0.4 ? "medium" : "low";
                setAnalysisScore({
                  value: avgScore,
                  confidence: avgScore,
                  level,
                  degradationFactors:
                    data.status === "partial"
                      ? ["Partial discovery — some scopes unavailable"]
                      : [],
                  dimensions: mergedDimensions,
                  resourceCount: totalResources,
                });
              } catch {
                // Scores fetch failed — show minimal result rather than nothing
                setAnalysisScore({
                  value: 0,
                  level: "low",
                  degradationFactors: ["Score data unavailable"],
                  dimensions: {},
                  resourceCount: 0,
                });
              }
              setCompletedRunId(runId);
              // Delay clearing activeRunId so the SignalR RunCompleted event
              // has time to arrive before the connection is torn down.
              setTimeout(() => {
                if (activePollTokenRef.current === token) {
                  setActiveRunId(null);
                }
              }, SIGNALR_EVENT_BUFFER_MS);
              resolve();
            } else if (
              data.status === "failed" ||
              data.status === "cancelled"
            ) {
              setError(
                `Analysis ${data.status}. Check service group scopes and managed identity permissions.`,
              );
              setActiveRunId(null);
              resolve();
            } else if (data.status === "running" || data.status === "queued") {
              setTimeout(checkStatus, retryAfterMs);
            }
          } catch (err: unknown) {
            log.error("Failed to poll analysis status:", { error: err, correlationId });
            setError(
              err instanceof Error
                ? err.message
                : "Failed to retrieve analysis status",
            );
            setActiveRunId(null);
            resolve();
          }
        };

        void checkStatus();
      });
    },
    [accessToken],
  );

  const startAnalysis = useCallback(
    async (groupId: string) => {
      const correlationId = crypto.randomUUID();
      activePollTokenRef.current = null;
      setError(undefined);
      setAnalyzingGroupId(groupId);
      setCompletedRunId(null);

      try {
        const resultResponse = await controlPlaneApi.startAnalysisWithMetadata(
          groupId,
          accessToken,
          correlationId,
        );
        const result = resultResponse.data;
        if (!result.runId) {
          throw new Error("Analysis response did not include run ID");
        }
        setActiveRunId(result.runId);
        await pollAnalysisStatus(groupId, result.runId, correlationId);
      } catch (err: unknown) {
        log.error("Failed to start analysis:", { error: err, correlationId });
        setError(
          err instanceof Error ? err.message : "Failed to start analysis",
        );
      } finally {
        setAnalyzingGroupId(null);
      }
    },
    [accessToken, pollAnalysisStatus],
  );

  return {
    analysisScore,
    analyzingGroupId,
    activeRunId,
    completedRunId,
    error,
    startAnalysis,
  };
}
