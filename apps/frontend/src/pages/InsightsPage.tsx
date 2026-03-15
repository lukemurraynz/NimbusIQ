import { useEffect, useMemo, useState } from "react";
import { useParams, useNavigate } from "react-router-dom";
import {
  Text,
  makeStyles,
  tokens,
  Button,
  Card,
  Badge,
  Spinner,
  Divider,
  Dropdown,
  Option,
  ProgressBar,
} from "@fluentui/react-components";
import {
  ArrowLeft24Regular,
  ArrowTrendingLines24Regular,
  Info24Regular,
  Shield24Regular,
  LeafTwo24Regular,
  MoneyHand24Regular,
  HeartPulse24Regular,
} from "@fluentui/react-icons";
import {
  LineChart,
  Line,
  XAxis,
  YAxis,
  Tooltip as ChartTooltip,
  ResponsiveContainer,
  CartesianGrid,
} from "recharts";
import { AzurePageHeader } from "../components/AzurePageHeader";
import { useAccessToken } from "../auth/useAccessToken";
import { useScoreHistory } from "../hooks/useScoreHistory";
import { useServiceGroupMetrics } from "../hooks/useServiceGroupMetrics";
import {
  controlPlaneApi,
  type ScoreExplainabilityResponse,
} from "../services/controlPlaneApi";

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
  mainGrid: {
    display: "grid",
    gridTemplateColumns: "1fr 1fr",
    gap: tokens.spacingHorizontalXL,
    "@media (max-width: 1200px)": {
      gridTemplateColumns: "1fr",
    },
  },
  scoreCard: {
    padding: tokens.spacingHorizontalXL,
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalM,
  },
  scoreHeader: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "center",
  },
  scoreValueRow: {
    display: "flex",
    alignItems: "baseline",
    gap: "8px",
  },
  scoreValue: {
    fontSize: tokens.fontSizeHero900,
    fontWeight: tokens.fontWeightBold,
    color: tokens.colorNeutralForeground1,
  },
  scoreDenom: {
    fontSize: tokens.fontSizeBase400,
    color: tokens.colorNeutralForeground3,
  },
  deltaPositive: {
    color: tokens.colorPaletteGreenForeground1,
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightSemibold,
  },
  deltaNegative: {
    color: tokens.colorPaletteRedForeground1,
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightSemibold,
  },
  chartContainer: {
    height: "320px",
    width: "100%",
    marginTop: tokens.spacingVerticalL,
  },
  dimensionGrid: {
    display: "grid",
    gridTemplateColumns: "1fr 1fr",
    gap: tokens.spacingVerticalM,
  },
  dimensionItem: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "center",
    padding: tokens.spacingVerticalS,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  impactGrid: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalS,
  },
  impactItem: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "center",
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorNeutralBackground3,
    borderRadius: tokens.borderRadiusMedium,
  },
  actionGrid: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalS,
  },
  actionItem: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "center",
    padding: tokens.spacingVerticalM,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  pillarsRow: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(140px, 1fr))",
    gap: tokens.spacingHorizontalM,
    marginTop: tokens.spacingVerticalM,
  },
  pillarBox: {
    padding: tokens.spacingVerticalM,
    backgroundColor: tokens.colorNeutralBackground3,
    borderRadius: tokens.borderRadiusMedium,
    textAlign: "center",
  },
  loadingContainer: {
    display: "flex",
    justifyContent: "center",
    alignItems: "center",
    minHeight: "400px",
  },
  iconLarge: {
    width: "48px",
    height: "48px",
    color: tokens.colorBrandForeground1,
  },
  targetSelector: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalM,
    marginTop: tokens.spacingVerticalL,
  },
});

const categoryColors: Record<string, string> = {
  Architecture: "#0078D4",
  FinOps: "#107C10",
  Reliability: "#D83B01",
  Sustainability: "#8764B8",
};

const categoryIcons: Record<string, React.ReactElement> = {
  Architecture: <Shield24Regular />,
  FinOps: <MoneyHand24Regular />,
  Reliability: <HeartPulse24Regular />,
  Sustainability: <LeafTwo24Regular />,
};

const scoringFormulas: Record<string, string> = {
  Architecture:
    "Score = 100 × (0.45 × metadata_completeness + 0.35 × availability + 0.20 × security)",
  FinOps:
    "Score = 100 × (0.50 × cost_efficiency + 0.30 × tagging_coverage + 0.20 × resource_utilization)",
  Reliability:
    "Score = 100 × (0.55 × availability + 0.25 × resiliency + 0.20 × security)",
  Sustainability:
    "Score = 100 × (0.50 × resource_utilization + 0.30 × carbon_signal + 0.20 × cost_efficiency)",
  Security:
    "Score = 100 × security_posture (identity, network isolation, data protection)",
};

export function InsightsPage() {
  const { pillar } = useParams<{ pillar: string }>();
  const navigate = useNavigate();
  const styles = useStyles();
  const { accessToken } = useAccessToken();
  const [targetScore, setTargetScore] = useState<string>("80");
  const [explainability, setExplainability] =
    useState<ScoreExplainabilityResponse | null>(null);
  const [explainabilityLoading, setExplainabilityLoading] = useState(false);

  // Normalize pillar name
  const category = useMemo(() => {
    if (!pillar) return "Architecture";
    const normalized =
      pillar.charAt(0).toUpperCase() + pillar.slice(1).toLowerCase();
    return normalized === "Finops" ? "FinOps" : normalized;
  }, [pillar]);

  // Get latest service group from recent analyses
  const [latestServiceGroupId, setLatestServiceGroupId] = useState<
    string | undefined
  >();

  // Fetch metrics to get recent analyses
  const { analysisRuns, loading: metricsLoading } =
    useServiceGroupMetrics(accessToken);

  // Extract service group ID from the most recent analysis once metrics load
  useEffect(() => {
    if (!metricsLoading && analysisRuns && analysisRuns.length > 0) {
      setLatestServiceGroupId(analysisRuns[0].serviceGroupId);
    }
  }, [analysisRuns, metricsLoading]);

  const serviceGroupId = latestServiceGroupId;

  // Fetch score history
  const {
    points,
    latestByCategory,
    loading: historyLoading,
  } = useScoreHistory(serviceGroupId, { category, limit: 30 }, accessToken);

  useEffect(() => {
    if (!serviceGroupId) {
      setExplainability(null);
      setExplainabilityLoading(false);
      return;
    }

    let cancelled = false;
    const requestedTarget = Number.parseInt(targetScore, 10) || 80;

    setExplainabilityLoading(true);

    void controlPlaneApi
      .getScoreExplainability(
        serviceGroupId,
        category,
        requestedTarget,
        accessToken,
        crypto.randomUUID(),
      )
      .then((result) => {
        if (!cancelled) {
          setExplainability(result);
        }
      })
      .catch(() => {
        if (!cancelled) {
          setExplainability(null);
        }
      })
      .finally(() => {
        if (!cancelled) {
          setExplainabilityLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [serviceGroupId, category, targetScore, accessToken]);

  const loading = metricsLoading || historyLoading || explainabilityLoading;

  const latest = latestByCategory[category];

  const delta = useMemo(() => {
    if (!latest?.deltaFromPrevious) return null;
    try {
      return JSON.parse(latest.deltaFromPrevious) as {
        previousScore: number;
        delta: number;
      };
    } catch {
      return null;
    }
  }, [latest]);

  const snapshotExplainability = useMemo(() => {
    if (!latest?.dimensions) return null;
    try {
      return JSON.parse(latest.dimensions) as Record<string, unknown>;
    } catch {
      return null;
    }
  }, [latest]);

  const wafPillars = useMemo(() => {
    if (explainability?.wafPillarScores) {
      return explainability.wafPillarScores;
    }

    if (!snapshotExplainability?.wafPillars) return null;
    return snapshotExplainability.wafPillars as Record<string, number>;
  }, [explainability, snapshotExplainability]);

  const contributingDimensions = useMemo(() => {
    if (explainability?.contributingDimensions) {
      return explainability.contributingDimensions;
    }

    if (
      snapshotExplainability?.dimensions &&
      typeof snapshotExplainability.dimensions === "object"
    ) {
      return snapshotExplainability.dimensions as Record<string, number>;
    }

    return null;
  }, [explainability, snapshotExplainability]);

  const topImpactFactors = useMemo(() => {
    if (explainability?.topContributors) {
      return explainability.topContributors;
    }

    if (!snapshotExplainability?.topImpactFactors) return null;
    return (
      snapshotExplainability.topImpactFactors as Array<{
        factor: string;
        severity: string;
        count?: number;
        affectedResources?: number;
      }>
    ).map((factor) => ({
      factor: factor.factor,
      severity: factor.severity,
      count: factor.count ?? factor.affectedResources ?? 0,
      impact: 0,
    }));
  }, [explainability, snapshotExplainability]);

  const methodology = useMemo(() => {
    switch (category) {
      case "Architecture":
        return "Based on weighted metadata completeness (tags, region, SKU, kind) and availability posture across discovered resources.";
      case "FinOps":
        return "Based on cost-efficiency signals, rightsizing opportunities, and billable resource coverage.";
      case "Reliability":
        return "Based on availability and security resilience indicators, weighted by impacted resource count.";
      case "Sustainability":
        return "Based on resource efficiency, green-region deployment ratio, and cost efficiency, with carbon telemetry layered in when available.";
      default:
        return "Based on heuristic analysis of resource metadata and governance findings.";
    }
  }, [category]);

  const primaryDriver = useMemo(() => {
    const rawDimensions = contributingDimensions;
    if (!rawDimensions || Object.keys(rawDimensions).length === 0) return null;
    const [lowestName, lowestValue] = Object.entries(rawDimensions).reduce(
      (lowest, current) => (current[1] < lowest[1] ? current : lowest),
    );
    return `${lowestName.replace(/_/g, " ")} is the lowest contributing dimension at ${Math.round(lowestValue * 100)}%.`;
  }, [contributingDimensions]);

  const chartData = useMemo(
    () =>
      [...points]
        .sort(
          (a, b) =>
            new Date(a.recordedAt).getTime() - new Date(b.recordedAt).getTime(),
        )
        .map((p) => ({
          date: new Date(p.recordedAt).toLocaleDateString(),
          score: p.score,
          confidence: Math.round(p.confidence * 100),
        })),
    [points],
  );

  const pathToTarget = useMemo(() => {
    const current = explainability?.currentScore ?? latest?.score ?? 0;
    const target = explainability?.targetScore ?? (parseInt(targetScore) || 80);
    const recommendationActions = (explainability?.pathToTarget ?? []).map(
      (
        action,
      ): {
        action: string;
        estImpact: number;
        effort: "S" | "M" | "L";
      } => ({
        action: action.action,
        estImpact: action.estimatedImpact,
        effort:
          action.effort === "Low" ? "S" : action.effort === "High" ? "L" : "M",
      }),
    );

    const cumulative = recommendationActions.reduce(
      (sum, action) => sum + action.estImpact,
      0,
    );
    const projected = Math.min(100, current + cumulative);

    return {
      current,
      target,
      projected,
      actions: recommendationActions,
    };
  }, [explainability, latest?.score, targetScore]);

  const currentScore = explainability?.currentScore ?? latest?.score ?? 0;
  const isGood = currentScore >= 80;
  const isOk = currentScore >= 60;

  const scoringFormula =
    explainability?.scoringFormula ?? scoringFormulas[category];

  if (loading) {
    return (
      <div className={styles.container}>
        <AzurePageHeader
          title={`${category} Score Insights`}
          subtitle="Loading score explainability..."
          commands={
            <Button
              appearance="subtle"
              icon={<ArrowLeft24Regular />}
              onClick={() => navigate("/")}
            >
              Back to Dashboard
            </Button>
          }
        />
        <div className={styles.content}>
          <div className={styles.loadingContainer}>
            <Spinner size="large" label={`Loading ${category} score data...`} />
          </div>
        </div>
      </div>
    );
  }

  if (!serviceGroupId) {
    return (
      <div className={styles.container}>
        <AzurePageHeader
          title={`${category} Score Insights`}
          subtitle="No analysis data available"
          commands={
            <Button
              appearance="subtle"
              icon={<ArrowLeft24Regular />}
              onClick={() => navigate("/")}
            >
              Back to Dashboard
            </Button>
          }
        />
        <div className={styles.content}>
          <Card className={styles.scoreCard}>
            <div
              style={{
                textAlign: "center",
                padding: tokens.spacingVerticalXXL,
              }}
            >
              <div className={styles.iconLarge} style={{ margin: "0 auto" }}>
                {categoryIcons[category]}
              </div>
              <Text
                size={500}
                weight="semibold"
                block
                style={{ marginTop: tokens.spacingVerticalL }}
              >
                No Analysis Data Available
              </Text>
              <Text
                size={300}
                block
                style={{
                  marginTop: tokens.spacingVerticalM,
                  color: tokens.colorNeutralForeground3,
                }}
              >
                Run an analysis on a service group to see detailed score
                breakdowns, trends, and improvement paths.
              </Text>
              <Button
                appearance="primary"
                style={{ marginTop: tokens.spacingVerticalXL }}
                onClick={() => navigate("/service-groups")}
              >
                Analyze Service Group
              </Button>
            </div>
          </Card>
        </div>
      </div>
    );
  }

  return (
    <div className={styles.container}>
      <AzurePageHeader
        title={`${category} Score Insights`}
        subtitle={`Explainability, trends, and improvement paths for ${category} pillar`}
        commands={
          <Button
            appearance="subtle"
            icon={<ArrowLeft24Regular />}
            onClick={() => navigate("/")}
          >
            Back to Dashboard
          </Button>
        }
      />
      <div className={styles.content}>
        {/* Current Score Summary */}
        <Card className={styles.scoreCard}>
          <div className={styles.scoreHeader}>
            <div
              style={{
                display: "flex",
                alignItems: "center",
                gap: tokens.spacingHorizontalM,
              }}
            >
              <div className={styles.iconLarge}>{categoryIcons[category]}</div>
              <div>
                <Text weight="semibold" size={500}>
                  Current {category} Score
                </Text>
                <Text
                  size={300}
                  style={{ color: tokens.colorNeutralForeground3 }}
                >
                  {methodology}
                </Text>
              </div>
            </div>
            <Badge
              appearance="tint"
              color={isGood ? "success" : isOk ? "warning" : "danger"}
              size="large"
            >
              {isGood ? "Good" : isOk ? "Fair" : "Needs Attention"}
            </Badge>
          </div>

          <Divider />

          <div className={styles.scoreValueRow}>
            <Text className={styles.scoreValue}>{currentScore}</Text>
            <Text className={styles.scoreDenom}>/100</Text>
            {delta && (
              <Badge
                appearance="tint"
                color={delta.delta >= 0 ? "success" : "danger"}
                size="large"
              >
                {delta.delta >= 0 ? "+" : ""}
                {delta.delta.toFixed(1)} from previous
              </Badge>
            )}
          </div>

          <ProgressBar
            value={currentScore / 100}
            color={isGood ? "success" : isOk ? "warning" : "error"}
            thickness="large"
          />

          {primaryDriver && (
            <div
              style={{
                display: "flex",
                alignItems: "start",
                gap: tokens.spacingHorizontalS,
                marginTop: tokens.spacingVerticalM,
              }}
            >
              <Info24Regular
                style={{ flexShrink: 0, color: tokens.colorBrandForeground1 }}
              />
              <Text
                size={300}
                style={{ color: tokens.colorNeutralForeground2 }}
              >
                {primaryDriver}
              </Text>
            </div>
          )}
        </Card>

        {/* Main content grid */}
        <div className={styles.mainGrid}>
          {/* Score Trend Chart */}
          <Card className={styles.scoreCard}>
            <div
              style={{
                display: "flex",
                alignItems: "center",
                gap: tokens.spacingHorizontalS,
              }}
            >
              <ArrowTrendingLines24Regular />
              <Text weight="semibold" size={400}>
                Score History (Last 30 Days)
              </Text>
            </div>

            {chartData.length > 0 ? (
              <div className={styles.chartContainer}>
                <ResponsiveContainer width="100%" height="100%">
                  <LineChart data={chartData}>
                    <CartesianGrid
                      strokeDasharray="3 3"
                      stroke={tokens.colorNeutralStroke2}
                    />
                    <XAxis
                      dataKey="date"
                      stroke={tokens.colorNeutralForeground3}
                      tick={{ fontSize: 12 }}
                    />
                    <YAxis
                      domain={[0, 100]}
                      stroke={tokens.colorNeutralForeground3}
                      tick={{ fontSize: 12 }}
                    />
                    <ChartTooltip
                      contentStyle={{
                        backgroundColor: tokens.colorNeutralBackground1,
                        border: `1px solid ${tokens.colorNeutralStroke1}`,
                        borderRadius: tokens.borderRadiusMedium,
                      }}
                    />
                    <Line
                      type="monotone"
                      dataKey="score"
                      stroke={categoryColors[category]}
                      strokeWidth={3}
                      dot={{ r: 4 }}
                      activeDot={{ r: 6 }}
                      name={`${category} Score`}
                    />
                  </LineChart>
                </ResponsiveContainer>
              </div>
            ) : (
              <Text
                size={300}
                style={{
                  color: tokens.colorNeutralForeground3,
                  textAlign: "center",
                  padding: tokens.spacingVerticalXXL,
                }}
              >
                No historical data available. Run multiple analyses to see
                trends.
              </Text>
            )}
          </Card>

          {/* Scoring Formula */}
          <Card className={styles.scoreCard}>
            <Text weight="semibold" size={400}>
              Scoring Formula
            </Text>
            <Text
              size={300}
              style={{
                fontFamily: "monospace",
                backgroundColor: tokens.colorNeutralBackground3,
                padding: tokens.spacingVerticalM,
                borderRadius: tokens.borderRadiusMedium,
              }}
            >
              {scoringFormula}
            </Text>

            {wafPillars && (
              <>
                <Divider />
                <Text weight="semibold" size={300}>
                  Well-Architected Framework Alignment
                </Text>
                <div className={styles.pillarsRow}>
                  {Object.entries(wafPillars).map(([pillar, score]) => (
                    <div key={pillar} className={styles.pillarBox}>
                      <Text
                        size={200}
                        style={{
                          color: tokens.colorNeutralForeground3,
                          textTransform: "capitalize",
                        }}
                      >
                        {pillar}
                      </Text>
                      <Text size={400} weight="semibold">
                        {Math.round(score * 100)}%
                      </Text>
                    </div>
                  ))}
                </div>
              </>
            )}
          </Card>

          {/* Contributing Dimensions */}
          {contributingDimensions ? (
            <Card className={styles.scoreCard}>
              <Text weight="semibold" size={400}>
                Contributing Dimensions
              </Text>
              <Text
                size={300}
                style={{ color: tokens.colorNeutralForeground3 }}
              >
                Component scores weighted to produce the final {category} score
              </Text>

              <div className={styles.dimensionGrid}>
                {Object.entries(contributingDimensions).map(([name, value]) => (
                  <div key={name} className={styles.dimensionItem}>
                    <Text style={{ textTransform: "capitalize" }}>
                      {name.replace(/_/g, " ")}
                    </Text>
                    <div
                      style={{
                        display: "flex",
                        alignItems: "center",
                        gap: tokens.spacingHorizontalS,
                      }}
                    >
                      <ProgressBar
                        value={value}
                        color={
                          value >= 0.8
                            ? "success"
                            : value >= 0.6
                              ? "warning"
                              : "error"
                        }
                        thickness="medium"
                        style={{ width: "80px" }}
                      />
                      <Text weight="semibold">{Math.round(value * 100)}%</Text>
                    </div>
                  </div>
                ))}
              </div>
            </Card>
          ) : null}

          {/* Top Impact Factors */}
          {topImpactFactors && topImpactFactors.length > 0 && (
            <Card className={styles.scoreCard}>
              <Text weight="semibold" size={400}>
                Top Impact Factors
              </Text>
              <Text
                size={300}
                style={{ color: tokens.colorNeutralForeground3 }}
              >
                Issues most affecting your {category} score
              </Text>

              <div className={styles.impactGrid}>
                {topImpactFactors.map((factor, idx) => (
                  <div key={idx} className={styles.impactItem}>
                    <div>
                      <Text weight="semibold">{factor.factor}</Text>
                      <Text
                        size={200}
                        style={{ color: tokens.colorNeutralForeground3 }}
                      >
                        {factor.count} occurrence{factor.count > 1 ? "s" : ""}
                      </Text>
                    </div>
                    <Badge
                      appearance="tint"
                      color={
                        factor.severity === "Critical"
                          ? "danger"
                          : factor.severity === "High"
                            ? "warning"
                            : "informative"
                      }
                    >
                      {factor.severity}
                    </Badge>
                  </div>
                ))}
              </div>
            </Card>
          )}
        </div>

        {/* Path to Target */}
        {pathToTarget.actions.length > 0 && (
          <Card
            className={styles.scoreCard}
            style={{ marginTop: tokens.spacingHorizontalXL }}
          >
            <div
              style={{
                display: "flex",
                justifyContent: "space-between",
                alignItems: "center",
              }}
            >
              <div>
                <Text weight="semibold" size={500}>
                  Path to Target Score
                </Text>
                <Text
                  size={300}
                  style={{ color: tokens.colorNeutralForeground3 }}
                >
                  Recommended actions to reach your goal
                </Text>
              </div>
              <div className={styles.targetSelector}>
                <Text size={300}>Target:</Text>
                <Dropdown
                  value={targetScore}
                  onOptionSelect={(_, data) =>
                    setTargetScore(data.optionValue ?? "80")
                  }
                  style={{ width: "100px" }}
                >
                  <Option value="70">70</Option>
                  <Option value="80">80</Option>
                  <Option value="90">90</Option>
                  <Option value="100">100</Option>
                </Dropdown>
              </div>
            </div>

            <Divider />

            <div
              style={{
                display: "grid",
                gridTemplateColumns: "1fr 1fr 1fr",
                gap: tokens.spacingHorizontalXL,
                marginBottom: tokens.spacingVerticalL,
              }}
            >
              <div>
                <Text
                  size={200}
                  style={{ color: tokens.colorNeutralForeground3 }}
                >
                  Current
                </Text>
                <Text size={500} weight="semibold">
                  {pathToTarget.current}
                </Text>
              </div>
              <div>
                <Text
                  size={200}
                  style={{ color: tokens.colorNeutralForeground3 }}
                >
                  Projected
                </Text>
                <Text
                  size={500}
                  weight="semibold"
                  style={{ color: tokens.colorPaletteGreenForeground1 }}
                >
                  {pathToTarget.projected}
                </Text>
              </div>
              <div>
                <Text
                  size={200}
                  style={{ color: tokens.colorNeutralForeground3 }}
                >
                  Gap to Target
                </Text>
                <Text size={500} weight="semibold">
                  {Math.max(0, pathToTarget.target - pathToTarget.projected)}
                </Text>
              </div>
            </div>

            <Text
              weight="semibold"
              size={400}
              style={{ marginBottom: tokens.spacingVerticalM }}
            >
              Recommended Actions
            </Text>

            <div className={styles.actionGrid}>
              {pathToTarget.actions.map((action, idx) => (
                <div key={idx} className={styles.actionItem}>
                  <div style={{ flex: 1 }}>
                    <Text weight="semibold">{action.action}</Text>
                    <Text
                      size={200}
                      style={{ color: tokens.colorNeutralForeground3 }}
                    >
                      Estimated impact: +{action.estImpact} points
                    </Text>
                  </div>
                  <Badge
                    appearance="tint"
                    color={
                      action.effort === "S"
                        ? "success"
                        : action.effort === "M"
                          ? "warning"
                          : "danger"
                    }
                  >
                    {action.effort === "S"
                      ? "Low Effort"
                      : action.effort === "M"
                        ? "Medium Effort"
                        : "High Effort"}
                  </Badge>
                </div>
              ))}
            </div>
          </Card>
        )}
      </div>
    </div>
  );
}
