import {
  Text,
  makeStyles,
  tokens,
  Button,
  Card,
  Badge,
  SkeletonItem,
  ProgressBar,
  Tooltip,
} from "@fluentui/react-components";
import {
  ArrowSyncCircle20Regular,
  ChartMultiple24Regular,
  Shield24Regular,
  DatabaseSearch24Regular,
  LeafTwo24Regular,
  Gavel24Regular,
  Rocket24Regular,
  MoneyHand24Regular,
  HeartPulse24Regular,
  CheckmarkCircle16Filled,
} from "@fluentui/react-icons";
import { useEffect, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import { useAccessToken } from "../auth/useAccessToken";
import { AzurePageHeader } from "../components/AzurePageHeader";
import { useServiceGroupMetrics } from "../hooks/useServiceGroupMetrics";
import { useAnalysisScores } from "../hooks/useAnalysisScores";
import { ExecutiveNarrativeBanner } from "../components/ExecutiveNarrativeBanner";
import { controlPlaneApi } from "../services/controlPlaneApi";
import { log } from "../telemetry/logger";
import type {
  CarbonEmissionsResponse,
  Recommendation,
  RecommendationQueueItem,
  RoiDashboardData,
} from "../services/controlPlaneApi";

const DANGEROUS_KEYS = new Set(["__proto__", "constructor", "prototype"]);

function formatCarbonFootprint(monthlyKgCO2e: number): {
  shortLabel: string;
  ariaLabel: string;
} {
  if (monthlyKgCO2e >= 1) {
    const formatted = monthlyKgCO2e.toFixed(1);
    return {
      shortLabel: `${formatted} kg CO₂e/mo`,
      ariaLabel: `${formatted} kilograms CO₂e per month`,
    };
  }

  if (monthlyKgCO2e >= 0.1) {
    const formatted = monthlyKgCO2e.toFixed(2);
    return {
      shortLabel: `${formatted} kg CO₂e/mo`,
      ariaLabel: `${formatted} kilograms CO₂e per month`,
    };
  }

  const grams = Math.max(1, Math.round(monthlyKgCO2e * 1000));
  return {
    shortLabel: `${grams} g CO₂e/mo`,
    ariaLabel: `${grams} grams CO₂e per month`,
  };
}

function findUrgentConflict(
  recommendations: Recommendation[],
): { first: Recommendation; second: Recommendation } | null {
  const byResource = new Map<string, Recommendation[]>();

  for (const recommendation of recommendations) {
    if (!recommendation.resourceId) continue;
    const bucket = byResource.get(recommendation.resourceId) ?? [];
    bucket.push(recommendation);
    byResource.set(recommendation.resourceId, bucket);
  }

  for (const recs of byResource.values()) {
    const sorted = [...recs].sort(
      (a, b) =>
        Number(b.riskWeightedScore ?? 0) - Number(a.riskWeightedScore ?? 0),
    );

    for (let i = 0; i < sorted.length; i++) {
      for (let j = i + 1; j < sorted.length; j++) {
        const first = sorted[i];
        const second = sorted[j];
        const sameCategory =
          (first.category ?? "") === (second.category ?? "");
        const sameAction =
          (first.actionType ?? "") === (second.actionType ?? "");

        if (!sameCategory || !sameAction) {
          return { first, second };
        }
      }
    }
  }

  return null;
}

const useStyles = makeStyles({
  container: {
    display: "flex",
    flexDirection: "column",
    height: "100%",
  },
  content: {
    flex: 1,
    overflow: "auto",
    padding: tokens.spacingHorizontalXXL,
  },
  metricsGrid: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(280px, 1fr))",
    gap: tokens.spacingHorizontalL,
    marginBottom: tokens.spacingVerticalXXL,
  },
  metricCard: {
    padding: tokens.spacingHorizontalXL,
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalM,
    minHeight: "140px",
    cursor: "pointer",
    transition: "all 0.2s ease",
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  scoreCard: {
    padding: tokens.spacingHorizontalXL,
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalS,
    minHeight: "140px",
    cursor: "pointer",
    transition: "all 0.2s ease",
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  scoreHeader: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "center",
  },
  scoreValueRow: {
    display: "flex",
    alignItems: "baseline",
    gap: "4px",
  },
  scoreValue: {
    fontSize: "32px",
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: "1",
    color: tokens.colorNeutralForeground1,
  },
  scoreDenom: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },
  metricHeader: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalM,
  },
  metricIcon: {
    width: "48px",
    height: "48px",
    borderRadius: tokens.borderRadiusMedium,
    display: "flex",
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: tokens.colorNeutralBackground3,
    color: tokens.colorBrandForeground1,
  },
  metricValue: {
    fontSize: "32px",
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: "1",
    color: tokens.colorNeutralForeground1,
  },
  metricLabel: {
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground3,
  },
  recentActivity: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalM,
  },
  activityItem: {
    padding: tokens.spacingVerticalM,
    paddingLeft: tokens.spacingHorizontalL,
    borderLeft: `3px solid ${tokens.colorBrandStroke1}`,
    backgroundColor: tokens.colorNeutralBackground1,
    display: "flex",
    justifyContent: "space-between",
    alignItems: "center",
    cursor: "pointer",
  },
  loadingContainer: {
    display: "flex",
    justifyContent: "center",
    alignItems: "center",
    minHeight: "400px",
  },
  quickActions: {
    display: "flex",
    gap: tokens.spacingHorizontalM,
    marginTop: tokens.spacingVerticalL,
  },
  onboardingCard: {
    padding: tokens.spacingHorizontalXXL,
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    justifyContent: "center",
    gap: tokens.spacingVerticalL,
    textAlign: "center",
    minHeight: "360px",
    background: `linear-gradient(135deg, ${tokens.colorNeutralBackground1} 0%, ${tokens.colorNeutralBackground3} 100%)`,
    borderRadius: tokens.borderRadiusXLarge,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  onboardingIcon: {
    fontSize: "64px",
    color: tokens.colorBrandForeground1,
    opacity: 0.9,
  },
  onboardingFeatureGrid: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(200px, 1fr))",
    gap: tokens.spacingHorizontalM,
    marginTop: tokens.spacingVerticalM,
    width: "100%",
    maxWidth: "640px",
  },
  onboardingFeature: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalS,
    padding: tokens.spacingVerticalS,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
  },

  governanceCard: {
    padding: tokens.spacingHorizontalXL,
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalM,
    minHeight: "140px",
    cursor: "pointer",
    transition: "all 0.2s ease",
    border: `1px solid ${tokens.colorPalettePurpleBorderActive}`,
    background: `linear-gradient(135deg, ${tokens.colorNeutralBackground1} 0%, ${tokens.colorPalettePurpleBackground2} 100%)`,
  },
  scoreHint: {
    color: tokens.colorNeutralForeground3,
  },
  changeCard: {
    padding: tokens.spacingHorizontalL,
    marginBottom: tokens.spacingVerticalL,
  },
  changeGrid: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
    gap: tokens.spacingHorizontalM,
    marginTop: tokens.spacingVerticalS,
  },
  spotlightSection: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalM,
    marginBottom: tokens.spacingVerticalXXL,
  },
  spotlightGrid: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(250px, 1fr))",
    gap: tokens.spacingHorizontalM,
  },
  spotlightCard: {
    padding: tokens.spacingHorizontalL,
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalS,
    minHeight: "180px",
  },
  spotlightValue: {
    fontSize: tokens.fontSizeBase500,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },
  spotlightMeta: {
    color: tokens.colorNeutralForeground3,
  },
  spotlightActions: {
    display: "flex",
    gap: tokens.spacingHorizontalS,
    flexWrap: "wrap",
    marginTop: "auto",
  },
  deltaPositive: {
    color: tokens.colorPaletteGreenForeground1,
  },
  deltaNegative: {
    color: tokens.colorPaletteRedForeground1,
  },
});

/**
 * Azure Portal-style dashboard with metrics tiles and quick actions.
 * Follows Microsoft design system with blade navigation.
 */
export function DashboardPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const { accessToken } = useAccessToken();
  // Stable ID per mount — avoids re-triggering useEffect on every render.
  const correlationId = useMemo(() => crypto.randomUUID(), []);

  useEffect(() => {
    document.title = "NimbusIQ — Dashboard";
  }, []);

  const {
    metrics: serviceMetrics,
    analysisRuns,
    loading: metricsLoading,
    error: metricsError,
    refresh: refreshMetrics,
  } = useServiceGroupMetrics(accessToken, correlationId);
  const { scores, loading: scoresLoading } = useAnalysisScores(
    analysisRuns,
    accessToken,
    correlationId,
  );

  const loading = metricsLoading || scoresLoading;
  const error = metricsError;

  const metrics = serviceMetrics ? { ...serviceMetrics, scores } : null;

  const latestServiceGroupId = useMemo(() => {
    const run = analysisRuns?.find(
      (r) => r.status === "completed" || r.status === "partial",
    );
    return run?.serviceGroupId;
  }, [analysisRuns]);

  // Carbon emissions for the Sustainability score card
  const [carbonEmissions, setCarbonEmissions] =
    useState<CarbonEmissionsResponse | null>(null);
  const [priorityQueue, setPriorityQueue] = useState<RecommendationQueueItem[]>(
    [],
  );
  const [roiDashboard, setRoiDashboard] = useState<RoiDashboardData | null>(
    null,
  );
  const [highestRiskDrift, setHighestRiskDrift] = useState<{
    score: number;
    violations: number;
    cause?: string;
  } | null>(null);
  const [urgentConflict, setUrgentConflict] = useState<{
    first: Recommendation;
    second: Recommendation;
  } | null>(null);
  const [scoreDeltas, setScoreDeltas] = useState<Record<string, number>>({});
  const [recommendationDelta, setRecommendationDelta] = useState<{
    newItems: number;
    resolvedItems: number;
  }>({ newItems: 0, resolvedItems: 0 });
  useEffect(() => {
    if (!latestServiceGroupId) {
      // Reset carbon emissions when service group changes to avoid stale data
      // eslint-disable-next-line react-hooks/set-state-in-effect
      setCarbonEmissions(null);
      return;
    }

    let cancelled = false;

    void controlPlaneApi
      .getCarbonEmissions(latestServiceGroupId, accessToken, correlationId)
      .then((data) => {
        if (cancelled) {
          return;
        }
        setCarbonEmissions(data);
      })
      .catch((err: unknown) => {
        if (cancelled) {
          return;
        }
        // Non-critical — carbon data is supplementary to the score
        log.warn("Failed to load carbon emissions data:", { error: err });
      });

    return () => {
      cancelled = true;
    };
  }, [latestServiceGroupId, accessToken, correlationId]);

  useEffect(() => {
    let cancelled = false;

    async function loadActionSpotlights() {
      try {
        const [queue, roi, recommendations, drift] = await Promise.all([
          controlPlaneApi.listRecommendationPriorityQueue(
            3,
            accessToken,
            correlationId,
          ),
          latestServiceGroupId
            ? controlPlaneApi.getValueTrackingDashboard(
                latestServiceGroupId,
                accessToken,
                correlationId,
              )
            : Promise.resolve(null),
          controlPlaneApi.listRecommendations(
            {
              status: "pending,pending_approval,manual_review",
              orderBy: "riskweighted",
              limit: 100,
            },
            accessToken,
            correlationId,
          ),
          latestServiceGroupId
            ? controlPlaneApi.getDriftStatus(
                latestServiceGroupId,
                accessToken,
                correlationId,
              )
            : Promise.resolve(null),
        ]);

        if (cancelled) return;

        setPriorityQueue(queue.value ?? []);
        setRoiDashboard(roi);
        setHighestRiskDrift(
          drift
            ? {
                score: drift.driftScore,
                violations: drift.totalViolations,
                cause: drift.causeType,
              }
            : null,
        );
        setUrgentConflict(findUrgentConflict(recommendations.value ?? []));
      } catch {
        if (cancelled) return;
        setPriorityQueue([]);
        setRoiDashboard(null);
        setHighestRiskDrift(null);
        setUrgentConflict(null);
      }
    }

    void loadActionSpotlights();

    return () => {
      cancelled = true;
    };
  }, [accessToken, correlationId, latestServiceGroupId]);

  useEffect(() => {
    if (!latestServiceGroupId) {
      // Reset score deltas when service group changes to avoid stale data
      // eslint-disable-next-line react-hooks/set-state-in-effect
      setScoreDeltas({});

      setRecommendationDelta({ newItems: 0, resolvedItems: 0 });
      return;
    }
    const serviceGroupId = latestServiceGroupId;

    let cancelled = false;

    async function loadChanges() {
      try {
        const history = await controlPlaneApi.getScoreHistory(
          serviceGroupId,
          { limit: 40 },
          accessToken,
          correlationId,
        );
        if (cancelled) return;

        const deltas: Record<string, number> = {};
        const grouped = new Map<
          string,
          Array<{ score: number; recordedAt: string }>
        >();
        for (const point of history.value ?? []) {
          const category = point.category?.toLowerCase() ?? "unknown";
          const bucket = grouped.get(category) ?? [];
          bucket.push({ score: point.score, recordedAt: point.recordedAt });
          grouped.set(category, bucket);
        }

        for (const [category, points] of grouped.entries()) {
          const sorted = [...points].sort(
            (a, b) =>
              new Date(b.recordedAt).getTime() -
              new Date(a.recordedAt).getTime(),
          );
          if (sorted.length >= 2) {
            deltas[category] = sorted[0].score - sorted[1].score;
          }
        }

        setScoreDeltas(deltas);
      } catch {
        setScoreDeltas({});
      }

      try {
        const completed = [...(analysisRuns ?? [])].filter(
          (r) => r.status === "completed" || r.status === "partial",
        );
        if (completed.length < 2) {
          if (!cancelled)
            setRecommendationDelta({ newItems: 0, resolvedItems: 0 });
          return;
        }
        const sortedRuns = completed.sort(
          (a, b) =>
            new Date(b.initiatedAt ?? b.createdAt).getTime() -
            new Date(a.initiatedAt ?? a.createdAt).getTime(),
        );
        const currentRun = sortedRuns[0];
        const previousRun = sortedRuns[1];
        const [current, previous] = await Promise.all([
          controlPlaneApi.listRecommendations(
            { analysisRunId: currentRun.id, limit: 200 },
            accessToken,
            correlationId,
          ),
          controlPlaneApi.listRecommendations(
            { analysisRunId: previousRun.id, limit: 200 },
            accessToken,
            correlationId,
          ),
        ]);
        if (cancelled) return;

        const currentIds = new Set((current.value ?? []).map((x) => x.id));
        const previousIds = new Set((previous.value ?? []).map((x) => x.id));
        let newItems = 0;
        let resolvedItems = 0;
        for (const id of currentIds) if (!previousIds.has(id)) newItems++;
        for (const id of previousIds) if (!currentIds.has(id)) resolvedItems++;
        setRecommendationDelta({ newItems, resolvedItems });
      } catch {
        if (!cancelled)
          setRecommendationDelta({ newItems: 0, resolvedItems: 0 });
      }
    }

    void loadChanges();

    return () => {
      cancelled = true;
    };
  }, [accessToken, analysisRuns, correlationId, latestServiceGroupId]);

  const openScoreDetails = (category: string) => {
    // Navigate to the full insights page instead of opening a blade
    const pillar = category.toLowerCase();
    navigate(`/insights/${pillar}`);
  };

  const handleRefresh = () => {
    refreshMetrics();
  };

  return (
    <div className={styles.container}>
      <AzurePageHeader
        title="NimbusIQ Dashboard"
        subtitle="Azure infrastructure governance and optimization insights"
        commands={
          <>
            <Button
              appearance="subtle"
              icon={<ArrowSyncCircle20Regular />}
              onClick={handleRefresh}
              disabled={loading}
            >
              Refresh
            </Button>
            <Button
              appearance="primary"
              onClick={() => navigate("/service-groups")}
            >
              Analyze Service Group
            </Button>
          </>
        }
      />

      <div className={styles.content}>
        {/* Executive narrative banner */}
        <ExecutiveNarrativeBanner
          serviceGroupId={latestServiceGroupId}
          accessToken={accessToken}
        />

        {loading && (
          <div
            className={styles.metricsGrid}
            role="status"
            aria-live="polite"
            aria-busy="true"
            aria-label="Loading dashboard metrics"
          >
            {Array.from({ length: 6 }, (_, i) => (
              <Card
                key={i}
                className={styles.metricCard}
                style={{ cursor: "default" }}
              >
                <div className={styles.metricHeader}>
                  <SkeletonItem shape="square" size={48} />
                  <SkeletonItem
                    shape="rectangle"
                    size={16}
                    style={{ width: "100px" }}
                  />
                </div>
                <SkeletonItem
                  shape="rectangle"
                  size={32}
                  style={{ width: "60px" }}
                />
                <SkeletonItem
                  shape="rectangle"
                  size={12}
                  style={{ width: "120px" }}
                />
              </Card>
            ))}
          </div>
        )}

        {!loading && error && (
          <Card role="alert" aria-live="assertive">
            <Text
              weight="semibold"
              block
              style={{ marginBottom: tokens.spacingVerticalS }}
            >
              Failed to load dashboard metrics
            </Text>
            <Text style={{ color: tokens.colorNeutralForeground3 }}>
              {error}
            </Text>
          </Card>
        )}

        {/* Onboarding state — no analysis runs yet */}
        {!loading &&
          !error &&
          metrics &&
          metrics.recentAnalyses.length === 0 && (
            <Card className={styles.onboardingCard}>
              <Rocket24Regular
                className={styles.onboardingIcon}
                aria-hidden="true"
              />
              <Text size={600} weight="semibold">
                Welcome to NimbusIQ
              </Text>
              <Text
                size={400}
                style={{
                  color: tokens.colorNeutralForeground3,
                  maxWidth: "480px",
                }}
              >
                NimbusIQ deploys 10 specialized AI agents to analyze your Azure
                infrastructure - covering architecture, reliability, cost,
                sustainability, drift, and governance.
              </Text>
              <div className={styles.onboardingFeatureGrid}>
                <div className={styles.onboardingFeature}>
                  <Shield24Regular aria-hidden="true" /> Well-Architected
                  Assessment
                </div>
                <div className={styles.onboardingFeature}>
                  <DatabaseSearch24Regular aria-hidden="true" /> Drift Detection
                </div>
                <div className={styles.onboardingFeature}>
                  <LeafTwo24Regular aria-hidden="true" /> Sustainability Scoring
                </div>
                <div className={styles.onboardingFeature}>
                  <Gavel24Regular aria-hidden="true" /> Governance Negotiation
                </div>
              </div>
              <Button
                appearance="primary"
                size="large"
                onClick={() => navigate("/service-groups")}
              >
                Run Your First Analysis
              </Button>
            </Card>
          )}

        {!loading && !error && metrics && metrics.recentAnalyses.length > 0 && (
          <>
            {/* Metrics Tiles */}
            <div
              className={styles.metricsGrid}
              role="region"
              aria-label="Dashboard metrics"
            >
              <Card
                className={styles.metricCard}
                onClick={() => navigate("/service-groups")}
                onKeyDown={(e) => {
                  if (e.key === "Enter" || e.key === " ") {
                    e.preventDefault();
                    navigate("/service-groups");
                  }
                }}
                tabIndex={0}
                role="button"
                aria-label="View analyzed service groups"
              >
                <div className={styles.metricHeader}>
                  <div className={styles.metricIcon} aria-hidden="true">
                    <ChartMultiple24Regular />
                  </div>
                  <div>
                    <Text className={styles.metricValue}>
                      {metrics.serviceGroups.analyzed}
                    </Text>
                    <Text className={styles.metricLabel}>
                      Analyzed Service Groups
                    </Text>
                  </div>
                </div>
                <Text
                  size={200}
                  style={{ color: tokens.colorNeutralForeground3 }}
                >
                  {metrics.serviceGroups.total} total service groups
                </Text>
              </Card>

              <Card
                className={styles.metricCard}
                onClick={() => navigate("/recommendations")}
                onKeyDown={(e) => {
                  if (e.key === "Enter" || e.key === " ") {
                    e.preventDefault();
                    navigate("/recommendations");
                  }
                }}
                tabIndex={0}
                role="button"
                aria-label="View pending recommendations"
              >
                <div className={styles.metricHeader}>
                  <div className={styles.metricIcon} aria-hidden="true">
                    <DatabaseSearch24Regular />
                  </div>
                  <div>
                    <Text className={styles.metricValue}>
                      {metrics.recommendations.pending}
                    </Text>
                    <Text className={styles.metricLabel}>
                      Pending Recommendations
                    </Text>
                  </div>
                </div>
                <Text
                  size={200}
                  style={{ color: tokens.colorNeutralForeground3 }}
                >
                  {metrics.recommendations.total} total •{" "}
                  {metrics.recommendations.approved} approved
                </Text>
              </Card>

              {/* WAF scores — one card per pillar with progress bar */}
              {(
                [
                  {
                    key: "architecture",
                    apiCategory: "Architecture",
                    label: "Architecture Score",
                    icon: <Shield24Regular />,
                    score: metrics.scores.architecture,
                  },
                  {
                    key: "sustainability",
                    apiCategory: "Sustainability",
                    label: "Sustainability Score",
                    icon: <LeafTwo24Regular />,
                    score: metrics.scores.sustainability,
                  },
                  {
                    key: "finops",
                    apiCategory: "FinOps",
                    label: "FinOps Score",
                    icon: <MoneyHand24Regular />,
                    score: metrics.scores.finops,
                  },
                  {
                    key: "reliability",
                    apiCategory: "Reliability",
                    label: "Reliability Score",
                    icon: <HeartPulse24Regular />,
                    score: metrics.scores.reliability,
                  },
                ] as const
              ).map(({ key, apiCategory, label, icon, score }) => {
                const pct = score / 100;
                const isGood = score >= 80;
                const isOk = score >= 60;
                const barColor = isGood
                  ? "success"
                  : isOk
                    ? "warning"
                    : "error";
                return (
                  <Card
                    key={key}
                    className={styles.scoreCard}
                    onClick={() => openScoreDetails(apiCategory)}
                    onKeyDown={(e) => {
                      if (e.key === "Enter" || e.key === " ") {
                        e.preventDefault();
                        openScoreDetails(apiCategory);
                      }
                    }}
                    tabIndex={0}
                    role="button"
                    aria-label={`${label}: ${score} out of 100`}
                  >
                    <div className={styles.scoreHeader}>
                      <div className={styles.metricHeader}>
                        <div
                          className={styles.metricIcon}
                          style={{ width: "36px", height: "36px" }}
                          aria-hidden="true"
                        >
                          {icon}
                        </div>
                        <Text className={styles.metricLabel}>{label}</Text>
                      </div>
                      <Badge
                        appearance="tint"
                        color={isGood ? "success" : isOk ? "warning" : "danger"}
                        size="small"
                      >
                        {isGood ? "Good" : isOk ? "Fair" : "Low"}
                      </Badge>
                    </div>
                    <div className={styles.scoreValueRow}>
                      <Text className={styles.scoreValue}>{score}</Text>
                      <Text className={styles.scoreDenom}>/100</Text>
                    </div>
                    <Tooltip
                      content={`${label}: ${score}/100`}
                      relationship="label"
                    >
                      <ProgressBar
                        value={pct}
                        color={barColor}
                        thickness="medium"
                      />
                    </Tooltip>
                    <Text size={200} className={styles.scoreHint}>
                      Click for score formula and contributing factors.
                    </Text>
                    {!DANGEROUS_KEYS.has(key) &&
                      typeof scoreDeltas[key] === "number" && (
                        <Text
                          size={200}
                          className={
                            scoreDeltas[key] >= 0
                              ? styles.deltaPositive
                              : styles.deltaNegative
                          }
                        >
                          {scoreDeltas[key] >= 0 ? "+" : ""}
                          {scoreDeltas[key].toFixed(1)} vs previous run
                        </Text>
                      )}
                    {key === "sustainability" && carbonEmissions && (
                      <Tooltip
                        content={
                          carbonEmissions.dataAvailabilityReason ??
                          (carbonEmissions.hasRealData
                            ? "Carbon data sourced from Azure Carbon Optimization API."
                            : "Carbon emissions telemetry unavailable for this run.")
                        }
                        relationship="description"
                      >
                        <Text
                          size={200}
                          style={{ color: tokens.colorNeutralForeground3 }}
                          aria-label={
                            carbonEmissions.hasRealData ||
                            carbonEmissions.monthlyKgCO2e > 0
                              ? `Carbon footprint: ${formatCarbonFootprint(carbonEmissions.monthlyKgCO2e).ariaLabel}${carbonEmissions.hasRealData ? "" : " (estimated)"}`
                              : "Carbon footprint telemetry unavailable for latest run"
                          }
                        >
                          {carbonEmissions.hasRealData ||
                          carbonEmissions.monthlyKgCO2e > 0 ? (
                            <>
                              🌿{" "}
                              {
                                formatCarbonFootprint(
                                  carbonEmissions.monthlyKgCO2e,
                                ).shortLabel
                              }
                              {carbonEmissions.hasRealData ? "" : " (est.)"}
                            </>
                          ) : (
                            <>🌿 Carbon telemetry unavailable</>
                          )}
                        </Text>
                      </Tooltip>
                    )}
                  </Card>
                );
              })}

              {/* Governance Negotiation — unique differentiator */}
              <Card
                className={styles.governanceCard}
                onClick={() => navigate("/governance")}
                onKeyDown={(e) => {
                  if (e.key === "Enter" || e.key === " ") {
                    e.preventDefault();
                    navigate("/governance");
                  }
                }}
                tabIndex={0}
                role="button"
                aria-label="View governance insights"
              >
                <div className={styles.metricHeader}>
                  <div
                    className={styles.metricIcon}
                    style={{
                      backgroundColor: tokens.colorPalettePurpleBackground2,
                      color: tokens.colorPalettePurpleForeground2,
                    }}
                    aria-hidden="true"
                  >
                    <Gavel24Regular />
                  </div>
                  <div>
                    <Text className={styles.metricValue}>
                      {metrics.recommendations.total}
                    </Text>
                    <Text className={styles.metricLabel}>
                      Governance Insights
                    </Text>
                  </div>
                </div>
                <Text
                  size={200}
                  style={{ color: tokens.colorNeutralForeground3 }}
                >
                  AI-mediated policy conflict resolution across cost, SLA, and
                  compliance
                </Text>
                <Badge appearance="tint" color="brand">
                  Unique to NimbusIQ
                </Badge>
              </Card>
            </div>

            <div className={styles.spotlightSection}>
              <Text size={500} weight="semibold">
                What needs action right now
              </Text>
              <div className={styles.spotlightGrid}>
                <Card className={styles.spotlightCard}>
                  <Text weight="semibold">Top 3 actions this week</Text>
                  <Text className={styles.spotlightValue}>
                    {priorityQueue.length}
                  </Text>
                  <Text className={styles.spotlightMeta}>
                    {priorityQueue.length > 0
                      ? priorityQueue
                          .map((item) => item.title ?? item.reason)
                          .slice(0, 3)
                          .join(" • ")
                      : "No urgent queue items detected."}
                  </Text>
                  <div className={styles.spotlightActions}>
                    <Button
                      appearance="secondary"
                      onClick={() => navigate("/recommendations")}
                    >
                      Open backlog
                    </Button>
                  </div>
                </Card>

                <Card className={styles.spotlightCard}>
                  <Text weight="semibold">Highest-risk drift</Text>
                  <Text className={styles.spotlightValue}>
                    {highestRiskDrift
                      ? `${highestRiskDrift.score.toFixed(1)}`
                      : "No data"}
                  </Text>
                  <Text className={styles.spotlightMeta}>
                    {highestRiskDrift
                      ? `${highestRiskDrift.violations} active violations${highestRiskDrift.cause ? ` • likely cause: ${highestRiskDrift.cause}` : ""}`
                      : "Run drift analysis to surface causal risk."}
                  </Text>
                  <div className={styles.spotlightActions}>
                    <Button
                      appearance="secondary"
                      onClick={() => navigate("/drift")}
                    >
                      Inspect drift
                    </Button>
                  </div>
                </Card>

                <Card className={styles.spotlightCard}>
                  <Text weight="semibold">Best ROI recommendation</Text>
                  <Text className={styles.spotlightValue}>
                    {roiDashboard?.topSavers?.[0]
                      ? `$${roiDashboard.topSavers[0].monthlySavings.toFixed(0)}/mo`
                      : "No data"}
                  </Text>
                  <Text className={styles.spotlightMeta}>
                    {roiDashboard?.topSavers?.[0]
                      ? roiDashboard.topSavers[0].title
                      : "Value realization appears once recommendations have estimated savings."}
                  </Text>
                  <div className={styles.spotlightActions}>
                    <Button
                      appearance="secondary"
                      onClick={() => navigate("/value-tracking")}
                    >
                      View ROI
                    </Button>
                  </div>
                </Card>

                <Card className={styles.spotlightCard}>
                  <Text weight="semibold">Most urgent governance conflict</Text>
                  <Text className={styles.spotlightValue}>
                    {urgentConflict ? "Conflict found" : "No conflict"}
                  </Text>
                  <Text className={styles.spotlightMeta}>
                    {urgentConflict
                      ? `${urgentConflict.first.title ?? urgentConflict.first.recommendationType} ↔ ${urgentConflict.second.title ?? urgentConflict.second.recommendationType}`
                      : "No competing recommendations detected on the same resource."}
                  </Text>
                  <div className={styles.spotlightActions}>
                    <Button
                      appearance="secondary"
                      onClick={() => navigate("/governance/conflicts")}
                    >
                      Review conflict
                    </Button>
                  </div>
                </Card>
              </div>
            </div>

            {/* Recent Activity */}
            <Card className={styles.changeCard}>
              <Text size={500} weight="semibold">
                What Changed Since Last Run
              </Text>
              <div className={styles.changeGrid}>
                <div>
                  <Text size={200} className={styles.scoreHint}>
                    New recommendations
                  </Text>
                  <Text weight="semibold">{recommendationDelta.newItems}</Text>
                </div>
                <div>
                  <Text size={200} className={styles.scoreHint}>
                    Resolved recommendations
                  </Text>
                  <Text weight="semibold">
                    {recommendationDelta.resolvedItems}
                  </Text>
                </div>
                <div>
                  <Text size={200} className={styles.scoreHint}>
                    Architecture delta
                  </Text>
                  <Text weight="semibold">
                    {(scoreDeltas.architecture ?? 0) >= 0 ? "+" : ""}
                    {(scoreDeltas.architecture ?? 0).toFixed(1)}
                  </Text>
                </div>
                <div>
                  <Text size={200} className={styles.scoreHint}>
                    Reliability delta
                  </Text>
                  <Text weight="semibold">
                    {(scoreDeltas.reliability ?? 0) >= 0 ? "+" : ""}
                    {(scoreDeltas.reliability ?? 0).toFixed(1)}
                  </Text>
                </div>
              </div>
            </Card>

            <Text
              size={500}
              weight="semibold"
              block
              style={{ marginBottom: tokens.spacingVerticalL }}
            >
              Recent Analyses
            </Text>
            <div className={styles.recentActivity}>
              {metrics.recentAnalyses.map((analysis) => {
                const isSuccess = analysis.status === "completed";
                const isPartial = analysis.status === "partial";
                const analysisPath = `/recommendations?analysisRunId=${analysis.id}`;
                return (
                  <div
                    key={analysis.id}
                    className={styles.activityItem}
                    onClick={() => navigate(analysisPath)}
                    onKeyDown={(e) => {
                      if (e.key === "Enter" || e.key === " ") {
                        e.preventDefault();
                        navigate(analysisPath);
                      }
                    }}
                    tabIndex={0}
                    role="button"
                    aria-label={`Open recommendations for ${analysis.name} run ${analysis.id}`}
                  >
                    <div
                      style={{
                        display: "flex",
                        alignItems: "center",
                        gap: tokens.spacingHorizontalM,
                      }}
                    >
                      <CheckmarkCircle16Filled
                        style={{
                          color: isSuccess
                            ? tokens.colorStatusSuccessForeground1
                            : isPartial
                              ? tokens.colorStatusWarningForeground1
                              : tokens.colorNeutralForeground3,
                          flexShrink: 0,
                        }}
                        aria-hidden="true"
                      />
                      <div>
                        <Text weight="semibold" block>
                          {analysis.name}
                        </Text>
                        <Text
                          size={200}
                          style={{ color: tokens.colorNeutralForeground3 }}
                        >
                          {analysis.timestamp}
                        </Text>
                      </div>
                    </div>
                    <Badge
                      appearance="filled"
                      color={
                        isSuccess
                          ? "success"
                          : isPartial
                            ? "warning"
                            : "informative"
                      }
                    >
                      {analysis.status}
                    </Badge>
                  </div>
                );
              })}
            </div>

            <div className={styles.quickActions}>
              <Button
                appearance="secondary"
                onClick={() => navigate("/timeline")}
              >
                View Evolution Timeline
              </Button>
              <Button appearance="secondary" onClick={() => navigate("/drift")}>
                View Drift Analysis
              </Button>
              <Button appearance="secondary" onClick={() => navigate("/chat")}>
                Ask AI Assistant
              </Button>
            </div>
          </>
        )}
      </div>
    </div>
  );
}
