import { useCallback, useEffect, useState } from "react";
import { controlPlaneApi } from "../services/controlPlaneApi";
import { log } from "../telemetry/logger";

export interface DriftSnapshot {
  id: string;
  serviceGroupId: string;
  snapshotTime: string;
  totalViolations: number;
  criticalViolations: number;
  highViolations: number;
  mediumViolations: number;
  lowViolations: number;
  driftScore: number;
  categoryBreakdown: string | null;
  trendAnalysis: string | null;
  createdAt: string;
  causeType?: string;
  causeActor?: string;
  causeSource?: string;
  causeEventTime?: string;
  causeConfidence?: number;
  causeEventId?: string;
  causeIsAuthoritative?: boolean;
}

export interface Violation {
  id: string;
  ruleId: string;
  ruleName: string;
  severity: string;
  category: string;
  driftCategory?: string;
  resourceId: string;
  resourceType: string;
  currentState: string;
  expectedState: string;
  detectedAt: string;
  status: string;
}

export type DayOption = "7" | "30" | "90";

/**
 * Custom hook to fetch drift data (snapshots, trends, violations).
 * Encapsulates complex Promise.allSettled logic and state management.
 */
export function useDriftData(
  serviceGroupId: string | undefined,
  timeRange: DayOption,
  accessToken?: string,
): {
  snapshots: DriftSnapshot[];
  violations: Violation[];
  currentSnapshot: DriftSnapshot | null;
  trendDirection: string;
  scoreChange: number;
  loading: boolean;
  error: string | null;
  refresh: () => void;
} {
  const [snapshots, setSnapshots] = useState<DriftSnapshot[]>([]);
  const [violations, setViolations] = useState<Violation[]>([]);
  const [currentSnapshot, setCurrentSnapshot] = useState<DriftSnapshot | null>(
    null,
  );
  const [trendDirection, setTrendDirection] = useState<string>("stable");
  const [scoreChange, setScoreChange] = useState<number>(0);
  const [loading, setLoading] = useState(false);
  const [error, setError] = useState<string | null>(null);
  const [refreshKey, setRefreshKey] = useState(0);

  const loadDriftData = useCallback(
    async (sgId: string, days: DayOption) => {
      if (!sgId) return;
      setLoading(true);
      setError(null);
      try {
        const correlationId = crypto.randomUUID();
        const daysNum = parseInt(days, 10);

        const [trends, status, violationsRes] = await Promise.allSettled([
          controlPlaneApi.getDriftTrends(
            sgId,
            daysNum,
            accessToken ?? undefined,
            correlationId,
          ),
          controlPlaneApi.getDriftStatus(
            sgId,
            accessToken ?? undefined,
            correlationId,
          ),
          controlPlaneApi.getViolations(
            sgId,
            { status: "active", limit: 200 },
            accessToken ?? undefined,
            correlationId,
          ),
        ]);

        if (trends.status === "fulfilled") {
          setSnapshots(trends.value.snapshots ?? []);
          setTrendDirection(trends.value.trendDirection ?? "stable");
          setScoreChange(trends.value.scoreChange ?? 0);
        }

        if (status.status === "fulfilled") {
          setCurrentSnapshot(status.value);
        } else {
          setCurrentSnapshot(null);
        }

        if (violationsRes.status === "fulfilled") {
          setViolations(violationsRes.value ?? []);
        }
      } catch (err: unknown) {
        log.error("Failed to load drift data", { error: err });
        setError("Failed to load drift data. Please try again.");
      } finally {
        setLoading(false);
      }
    },
    [accessToken],
  );

  useEffect(() => {
    if (serviceGroupId) {
      loadDriftData(serviceGroupId, timeRange);
    }
  }, [serviceGroupId, timeRange, loadDriftData, refreshKey]);

  return {
    snapshots,
    violations,
    currentSnapshot,
    trendDirection,
    scoreChange,
    loading,
    error,
    refresh: () => setRefreshKey((k) => k + 1),
  };
}
