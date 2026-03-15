import { useEffect, useMemo, useState } from "react";
import {
  Card,
  CardHeader,
  Text,
  Badge,
  makeStyles,
  tokens,
  Spinner,
  Divider,
  Dropdown,
  Option,
} from "@fluentui/react-components";
import {
  ArrowTrendingLines24Regular,
  Info24Regular,
} from "@fluentui/react-icons";
import {
  LineChart,
  Line,
  XAxis,
  YAxis,
  Tooltip,
  ResponsiveContainer,
  CartesianGrid,
} from "recharts";
import { useScoreHistory } from "../hooks/useScoreHistory";
import {
  controlPlaneApi,
  type ScoreExplainabilityResponse,
} from "../services/controlPlaneApi";

const useStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalL,
    padding: tokens.spacingHorizontalL,
  },
  scoreCard: {
    padding: tokens.spacingHorizontalM,
  },
  scoreHeader: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "center",
  },
  scoreValue: {
    fontSize: tokens.fontSizeHero900,
    fontWeight: tokens.fontWeightBold,
  },
  deltaPositive: {
    color: tokens.colorPaletteGreenForeground1,
  },
  deltaNegative: {
    color: tokens.colorPaletteRedForeground1,
  },
  chartContainer: {
    height: "240px",
    width: "100%",
  },
  dimensionGrid: {
    display: "grid",
    gridTemplateColumns: "1fr 1fr",
    gap: tokens.spacingVerticalS,
  },
  dimensionItem: {
    display: "flex",
    justifyContent: "space-between",
    padding: tokens.spacingVerticalXS,
  },
});

interface ScoreExplainabilityBladeProps {
  serviceGroupId: string;
  category: string;
  accessToken?: string;
}

const categoryColors: Record<string, string> = {
  Architecture: "#0078D4",
  FinOps: "#107C10",
  Reliability: "#D83B01",
  Sustainability: "#8764B8",
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

export function ScoreExplainabilityBlade({
  serviceGroupId,
  category,
  accessToken,
}: ScoreExplainabilityBladeProps) {
  const styles = useStyles();
  const [targetScore, setTargetScore] = useState<number>(80);
  const [explainability, setExplainability] =
    useState<ScoreExplainabilityResponse | null>(null);
  const [loadingExplainability, setLoadingExplainability] = useState(false);
  const [explainabilityError, setExplainabilityError] = useState<string | null>(
    null,
  );
  const { points, latestByCategory, loading } = useScoreHistory(
    serviceGroupId,
    { category, limit: 30 },
    accessToken,
  );

  const latest = latestByCategory[category];
  useEffect(() => {
    let cancelled = false;

    async function loadExplainability() {
      if (!serviceGroupId || !category) return;
      setLoadingExplainability(true);
      setExplainabilityError(null);

      try {
        const result = await controlPlaneApi.getScoreExplainability(
          serviceGroupId,
          category,
          targetScore,
          accessToken,
        );
        if (!cancelled) {
          setExplainability(result);
        }
      } catch (err) {
        if (!cancelled) {
          setExplainabilityError(
            err instanceof Error
              ? err.message
              : "Failed to load explainability details",
          );
        }
      } finally {
        if (!cancelled) {
          setLoadingExplainability(false);
        }
      }
    }

    void loadExplainability();

    return () => {
      cancelled = true;
    };
  }, [serviceGroupId, category, targetScore, accessToken]);

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

  const dimensions = useMemo(() => {
    if (!latest?.dimensions) return null;
    try {
      return JSON.parse(latest.dimensions) as Record<string, unknown>;
    } catch {
      return null;
    }
  }, [latest]);

  const wafPillars = useMemo(() => {
    if (explainability?.wafPillarScores) return explainability.wafPillarScores;
    if (!dimensions?.wafPillars) return null;
    return dimensions.wafPillars as Record<string, number>;
  }, [dimensions, explainability]);

  const topImpactFactors = useMemo(() => {
    if (explainability?.topContributors?.length) {
      return explainability.topContributors.map((c) => ({
        factor: c.factor,
        severity: c.severity,
        count: c.count,
      }));
    }
    if (!dimensions?.topImpactFactors) return null;
    return dimensions.topImpactFactors as Array<{
      factor: string;
      severity: string;
      count: number;
    }>;
  }, [dimensions, explainability]);

  const methodology = useMemo(() => {
    switch (category) {
      case "Architecture":
        return "Based on weighted metadata completeness (tags, region, SKU, kind) and availability posture across discovered resources.";
      case "FinOps":
        return "Based on cost-efficiency signals, rightsizing opportunities, and billable resource coverage.";
      case "Reliability":
        return "Based on availability, security resilience indicators, and observability coverage.";
      case "Sustainability":
        return "Based on resource efficiency, green-region deployment ratio, and cost efficiency.";
      case "Security":
        return "Based on security posture signals, network isolation indicators, and compliance metadata coverage.";
      default:
        return "Based on heuristic analysis of resource metadata and governance findings.";
    }
  }, [category]);

  const primaryDriver = useMemo(() => {
    const rawDimensions =
      (dimensions?.dimensions as Record<string, number>) ?? null;
    if (!rawDimensions || Object.keys(rawDimensions).length === 0) return null;
    const [lowestName, lowestValue] = Object.entries(rawDimensions).reduce(
      (lowest, current) => (current[1] < lowest[1] ? current : lowest),
    );
    return `${lowestName} is the lowest contributing dimension at ${Math.round(
      lowestValue * 100,
    )}%.`;
  }, [dimensions]);

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
    if (explainability) {
      const cumulative = explainability.pathToTarget.reduce(
        (sum, action) => sum + action.estimatedImpact,
        0,
      );

      return {
        current: explainability.currentScore,
        target: explainability.targetScore,
        gap: explainability.gap,
        projected: Math.min(100, explainability.currentScore + cumulative),
        actions: explainability.pathToTarget.map((a) => ({
          action: a.action,
          estImpact: a.estimatedImpact,
          effort: a.effort,
        })),
      };
    }

    return {
      current: latest?.score ?? 0,
      target: targetScore,
      gap: Math.max(0, targetScore - (latest?.score ?? 0)),
      projected: latest?.score ?? 0,
      actions: [] as Array<{
        action: string;
        estImpact: number;
        effort: string;
      }>,
    };
  }, [explainability, latest?.score, targetScore]);

  if (loading) {
    return (
      <div className={styles.root}>
        <Spinner label={`Loading ${category} score history...`} />
      </div>
    );
  }

  if (loadingExplainability && !explainability) {
    return (
      <div className={styles.root}>
        <Spinner label={`Loading ${category} explainability...`} />
      </div>
    );
  }

  return (
    <div className={styles.root}>
      {/* Current Score */}
      <Card className={styles.scoreCard}>
        <CardHeader
          header={<Text weight="semibold">{category} Score</Text>}
          action={
            delta ? (
              <Badge
                appearance="filled"
                color={delta.delta >= 0 ? "success" : "danger"}
              >
                {delta.delta >= 0 ? "+" : ""}
                {delta.delta} pts
              </Badge>
            ) : undefined
          }
        />
        <div className={styles.scoreHeader}>
          <Text className={styles.scoreValue}>{latest?.score ?? 0}</Text>
          <Text size={200}>
            Confidence:{" "}
            {latest ? `${Math.round(latest.confidence * 100)}%` : "N/A"}
          </Text>
        </div>
        {latest && (
          <Text size={200}>
            {latest.resourceCount} resources evaluated ·{" "}
            {new Date(latest.recordedAt).toLocaleString()}
          </Text>
        )}
      </Card>

      <Card>
        <CardHeader
          header={<Text weight="semibold">How This Score Is Marked</Text>}
        />
        <Text size={200}>{methodology}</Text>
        <Text size={200} weight="semibold">
          Formula
        </Text>
        <Text size={200}>
          {scoringFormulas[category] ??
            "Score = weighted average of normalized dimensions (0-1), scaled to 100."}
        </Text>
        {latest && (
          <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
            Contributing resources: {latest.resourceCount}
            {topImpactFactors && topImpactFactors.length > 0
              ? ` • contributing violations/signals: ${topImpactFactors.reduce((sum, f) => sum + (f.count ?? 0), 0)}`
              : ""}
          </Text>
        )}
        {primaryDriver && (
          <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
            {primaryDriver}
          </Text>
        )}
        {explainabilityError && (
          <Text size={200} style={{ color: tokens.colorPaletteRedForeground1 }}>
            {explainabilityError}
          </Text>
        )}
      </Card>

      {/* Trend Chart */}
      {chartData.length > 1 && (
        <Card>
          <CardHeader
            image={<ArrowTrendingLines24Regular />}
            header={<Text weight="semibold">Score Trend</Text>}
          />
          <div className={styles.chartContainer}>
            <ResponsiveContainer width="100%" height="100%">
              <LineChart data={chartData}>
                <CartesianGrid strokeDasharray="3 3" />
                <XAxis dataKey="date" tick={{ fontSize: 10 }} />
                <YAxis domain={[0, 100]} />
                <Tooltip />
                <Line
                  type="monotone"
                  dataKey="score"
                  stroke={categoryColors[category] ?? "#0078D4"}
                  strokeWidth={2}
                  dot={{ r: 3 }}
                />
              </LineChart>
            </ResponsiveContainer>
          </div>
        </Card>
      )}

      <Divider />

      {/* Sub-dimension Breakdown */}
      {dimensions && (
        <Card>
          <CardHeader
            image={<Info24Regular />}
            header={<Text weight="semibold">Dimension Breakdown</Text>}
          />
          <div className={styles.dimensionGrid}>
            {Object.entries(
              (dimensions.dimensions as Record<string, number>) ?? dimensions,
            )
              .filter(
                ([key]) =>
                  key !== "wafPillars" &&
                  key !== "topImpactFactors" &&
                  key !== "dimensions",
              )
              .map(([dim, val]) => (
                <div key={dim} className={styles.dimensionItem}>
                  <Text size={200}>{dim}</Text>
                  <Text size={200} weight="semibold">
                    {typeof val === "number"
                      ? `${Math.round(val * 100)}%`
                      : String(val)}
                  </Text>
                </div>
              ))}
          </div>
        </Card>
      )}

      {/* WAF Pillar Mapping */}
      {wafPillars && (
        <Card>
          <CardHeader
            header={<Text weight="semibold">WAF Pillar Scores</Text>}
          />
          <div className={styles.dimensionGrid}>
            {Object.entries(wafPillars).map(([pillar, score]) => {
              const pct =
                typeof score === "number" ? Math.round(score * 100) : 0;
              return (
                <div key={pillar} className={styles.dimensionItem}>
                  <Text size={200}>
                    {pillar.replace(/([A-Z])/g, " $1").trim()}
                  </Text>
                  <Badge
                    appearance="filled"
                    color={
                      pct >= 70 ? "success" : pct >= 40 ? "warning" : "danger"
                    }
                  >
                    {pct}%
                  </Badge>
                </div>
              );
            })}
          </div>
        </Card>
      )}

      {/* Top Impact Factors */}
      {topImpactFactors && topImpactFactors.length > 0 && (
        <Card>
          <CardHeader
            header={<Text weight="semibold">Top Impact Factors</Text>}
          />
          {topImpactFactors.map((f, i) => (
            <div
              key={i}
              style={{
                display: "flex",
                justifyContent: "space-between",
                alignItems: "center",
                padding: tokens.spacingVerticalXS,
              }}
            >
              <Text size={200}>{f.factor}</Text>
              <div style={{ display: "flex", gap: tokens.spacingHorizontalXS }}>
                <Badge
                  appearance="tint"
                  color={
                    f.severity === "Critical"
                      ? "danger"
                      : f.severity === "High"
                        ? "warning"
                        : "informative"
                  }
                >
                  {f.severity}
                </Badge>
                <Text size={200}>
                  {f.count} violation{f.count !== 1 ? "s" : ""}
                </Text>
                {explainability && (
                  <Badge appearance="outline">
                    Impact{" "}
                    {Math.abs(
                      explainability.topContributors.find(
                        (c) => c.factor === f.factor,
                      )?.impact ?? 0,
                    )}
                  </Badge>
                )}
              </div>
            </div>
          ))}
        </Card>
      )}

      {/* Delta Details */}
      {delta && (
        <Card>
          <CardHeader
            header={<Text weight="semibold">Change from Previous</Text>}
          />
          <Text size={200}>
            Previous score: {delta.previousScore} → Current:{" "}
            {latest?.score ?? 0} (
            <span
              className={
                delta.delta >= 0 ? styles.deltaPositive : styles.deltaNegative
              }
            >
              {delta.delta >= 0 ? "+" : ""}
              {delta.delta}
            </span>
            )
          </Text>
        </Card>
      )}

      <Card>
        <CardHeader header={<Text weight="semibold">Path to Target</Text>} />
        <Text size={200}>
          Plan improvements to move <strong>{category}</strong> from current
          score toward a selected target.
        </Text>
        <div style={{ marginTop: tokens.spacingVerticalS, maxWidth: "220px" }}>
          <Dropdown
            value={`Target ${targetScore}`}
            selectedOptions={[String(targetScore)]}
            onOptionSelect={(_, data) => {
              const next = Number(data.optionValue);
              if (Number.isFinite(next)) setTargetScore(next);
            }}
            aria-label="Select target score"
          >
            <Option value="70">Target 70</Option>
            <Option value="80">Target 80</Option>
            <Option value="90">Target 90</Option>
          </Dropdown>
        </div>
        <Text size={200} style={{ marginTop: tokens.spacingVerticalS }}>
          Current {pathToTarget.current} → Target {pathToTarget.target} (Gap{" "}
          {pathToTarget.gap})
        </Text>
        <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
          Projected after top actions: {pathToTarget.projected}
        </Text>
        {pathToTarget.actions.length > 0 ? (
          <div
            style={{
              marginTop: tokens.spacingVerticalS,
              display: "flex",
              flexDirection: "column",
              gap: tokens.spacingVerticalXS,
            }}
          >
            {pathToTarget.actions.map((a, idx) => (
              <div
                key={`${a.action}-${idx}`}
                style={{
                  display: "flex",
                  justifyContent: "space-between",
                  gap: tokens.spacingHorizontalS,
                }}
              >
                <Text size={200}>• {a.action}</Text>
                <div
                  style={{ display: "flex", gap: tokens.spacingHorizontalXS }}
                >
                  <Badge appearance="outline">+{a.estImpact}</Badge>
                  <Badge appearance="tint">{a.effort}</Badge>
                </div>
              </div>
            ))}
          </div>
        ) : (
          <Text size={200} style={{ marginTop: tokens.spacingVerticalS }}>
            No specific target actions available yet.
          </Text>
        )}
      </Card>
    </div>
  );
}
