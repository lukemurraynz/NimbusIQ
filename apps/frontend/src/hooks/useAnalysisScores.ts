import { useEffect, useState } from "react";
import { controlPlaneApi } from "../services/controlPlaneApi";

export interface Scores {
  architecture: number;
  finops: number;
  reliability: number;
  sustainability: number;
}

/**
 * Custom hook to fetch analysis scores.
 * Encapsulates logic for fetching and processing scores from the latest analysis run.
 */
export function useAnalysisScores(
  analysisRuns:
    | Array<{ serviceGroupId: string; status: string; id: string }>
    | null
    | undefined,
  accessToken?: string,
  correlationId?: string,
): {
  scores: Scores;
  loading: boolean;
  error?: string;
} {
  const [scores, setScores] = useState<Scores>({
    architecture: 0,
    finops: 0,
    reliability: 0,
    sustainability: 0,
  });
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | undefined>(undefined);

  useEffect(() => {
    let cancelled = false;

    async function fetchScores() {
      setLoading(true);
      setError(undefined);

      try {
        if (!analysisRuns || analysisRuns.length === 0) {
          setScores({
            architecture: 0,
            finops: 0,
            reliability: 0,
            sustainability: 0,
          });
          setLoading(false);
          return;
        }

        const latestScoredRun = analysisRuns.find(
          (r) => r.status === "completed" || r.status === "partial",
        );

        if (!latestScoredRun) {
          setScores({
            architecture: 0,
            finops: 0,
            reliability: 0,
            sustainability: 0,
          });
          setLoading(false);
          return;
        }

        const id = correlationId || crypto.randomUUID();

        try {
          const latestScores = await controlPlaneApi.getLatestScores(
            latestScoredRun.serviceGroupId,
            accessToken,
            id,
          );

          if (cancelled) return;

          if (latestScores?.value?.length) {
            const getScore = (name: string) =>
              latestScores.value.find(
                (s) => s.category?.toLowerCase() === name.toLowerCase(),
              )?.score ?? 0;

            setScores({
              architecture: getScore("architecture"),
              finops: getScore("finops"),
              reliability:
                getScore("reliability") ||
                Math.round(
                  (getScore("bestpractice") + getScore("wellarchitected")) / 2,
                ),
              sustainability:
                getScore("sustainability") || getScore("cloudnative"),
            });
          }
        } catch {
          // Non-fatal: fall back to zeros if scores endpoint is unavailable.
          setScores({
            architecture: 0,
            finops: 0,
            reliability: 0,
            sustainability: 0,
          });
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
    }

    void fetchScores();

    return () => {
      cancelled = true;
    };
  }, [analysisRuns, accessToken, correlationId]);

  return { scores, loading, error };
}
