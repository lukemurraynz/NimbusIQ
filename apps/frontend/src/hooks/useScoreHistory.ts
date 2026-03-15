import { useEffect, useState, useCallback } from "react";
import { controlPlaneApi, type ScorePoint } from "../services/controlPlaneApi";

export interface ScoreHistoryData {
  points: ScorePoint[];
  latestByCategory: Record<string, ScorePoint>;
  loading: boolean;
  error?: string;
  refetch: () => void;
}

export function useScoreHistory(
  serviceGroupId: string | undefined,
  options?: { category?: string; limit?: number; since?: string },
  accessToken?: string,
): ScoreHistoryData {
  const [points, setPoints] = useState<ScorePoint[]>([]);
  const [latestByCategory, setLatestByCategory] = useState<
    Record<string, ScorePoint>
  >({});
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | undefined>();
  const [fetchKey, setFetchKey] = useState(0);

  const refetch = useCallback(() => setFetchKey((k) => k + 1), []);

  // Destructure to primitives so the effect deps are stable even if the options
  // object reference changes on every render (e.g., inline literal from caller).
  const { category, limit, since } = options ?? {};

  useEffect(() => {
    if (!serviceGroupId) {
      setPoints([]);
      setLatestByCategory({});
      setLoading(false);
      return;
    }

    let cancelled = false;

    (async () => {
      setLoading(true);
      setError(undefined);
      try {
        const [historyRes, latestRes] = await Promise.all([
          controlPlaneApi.getScoreHistory(
            serviceGroupId,
            { category, limit, since },
            accessToken,
          ),
          controlPlaneApi.getLatestScores(serviceGroupId, accessToken),
        ]);

        if (cancelled) return;

        setPoints(historyRes.value);

        const byCategory: Record<string, ScorePoint> = {};
        for (const pt of latestRes.value) {
          byCategory[pt.category] = pt;
        }
        setLatestByCategory(byCategory);
      } catch (err) {
        if (!cancelled) {
          setError(
            err instanceof Error ? err.message : "Failed to load score history",
          );
        }
      } finally {
        if (!cancelled) setLoading(false);
      }
    })();

    return () => {
      cancelled = true;
    };
  }, [
    serviceGroupId,
    category,
    limit,
    since,
    accessToken,
    fetchKey,
  ]);

  return { points, latestByCategory, loading, error, refetch };
}
