import { useEffect, useState } from 'react';
import { controlPlaneApi, type AnalysisRun } from '../services/controlPlaneApi';

export interface DashboardMetrics {
  serviceGroups: { total: number; analyzed: number };
  recommendations: { pending: number; approved: number; total: number };
  recentAnalyses: Array<{ id: string; name: string; timestamp: string; status: string }>;
}

/**
 * Custom hook to fetch service group metrics and recent analyses.
 * Encapsulates parallel data fetching for service groups, recommendations, and analysis runs.
 */
export function useServiceGroupMetrics(
  accessToken?: string,
  correlationId?: string
): {
  metrics: Omit<DashboardMetrics, 'scores'> | null;
  analysisRuns: AnalysisRun[] | null;
  loading: boolean;
  error?: string;
  refresh: () => void;
} {
  const [metrics, setMetrics] = useState<Omit<DashboardMetrics, 'scores'> | null>(null);
  const [analysisRuns, setAnalysisRuns] = useState<AnalysisRun[] | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | undefined>(undefined);
  const [refreshKey, setRefreshKey] = useState(0);

  useEffect(() => {
    let cancelled = false;

    async function fetchMetrics() {
      setLoading(true);
      setError(undefined);
      try {
        const id = correlationId || crypto.randomUUID();

        const [serviceGroupsResult, recommendationsResult, analysisRunsResult] = await Promise.all([
          controlPlaneApi.listServiceGroups(accessToken, id),
          controlPlaneApi.listRecommendations(undefined, accessToken, id),
          controlPlaneApi.listAnalysisRuns({ limit: 5 }, accessToken, id),
        ]);

        if (cancelled) return;

        const groups: Array<{ id: string; name: string }> = (serviceGroupsResult.value ?? []) as Array<{ id: string; name: string }>;
        const recs = recommendationsResult.value ?? [];
        const runs: Array<{ id: string; serviceGroupId: string; status: string; initiatedAt: string }> = Array.isArray(analysisRunsResult)
          ? (analysisRunsResult as Array<{ id: string; serviceGroupId: string; status: string; initiatedAt: string }>)
          : [];

        const sgNameById = new Map(groups.map((g) => [g.id, g.name]));

        setAnalysisRuns(runs.slice(0, 5) as AnalysisRun[]);

        setMetrics({
          serviceGroups: { total: groups.length, analyzed: groups.length },
          recommendations: {
            pending: recs.filter((r) => r.status === 'pending' || r.status === 'PendingApproval').length,
            approved: recs.filter((r) => r.status === 'approved' || r.status === 'Approved').length,
            total: recs.length,
          },
          recentAnalyses: runs.slice(0, 5).map((run) => ({
            id: run.id,
            name: sgNameById.get(run.serviceGroupId) ?? `Service Group ${run.serviceGroupId.slice(0, 8)}…`,
            timestamp: new Date(run.initiatedAt).toLocaleString(),
            status: run.status,
          })),
        });
      } catch (e) {
        if (cancelled) return;
        setError(e instanceof Error ? e.message : String(e));
      } finally {
        if (!cancelled) setLoading(false);
      }
    }

    void fetchMetrics();

    return () => {
      cancelled = true;
    };
  }, [accessToken, correlationId, refreshKey]);

  return {
    metrics,
    analysisRuns,
    loading,
    error,
    refresh: () => setRefreshKey((k) => k + 1),
  };
}
