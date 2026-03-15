import { useEffect, useState, useCallback } from "react";
import { useSearchParams } from "react-router-dom";
import {
  Card,
  Text,
  makeStyles,
  tokens,
  Spinner,
  Badge,
  Button,
  Select,
  Textarea,
  Table,
  TableHeader,
  TableHeaderCell,
  TableBody,
  TableRow,
  TableCell,
} from "@fluentui/react-components";
import {
  Gavel20Regular,
  ErrorCircle20Regular,
  Checkmark20Regular,
  ArrowRight20Regular,
  Dismiss20Regular,
} from "@fluentui/react-icons";
import { useAccessToken } from "../auth/useAccessToken";
import { useNotify } from "../components/useNotify";
import {
  controlPlaneApi,
  type GovernanceNegotiationResult,
  type Recommendation,
} from "../services/controlPlaneApi";
import { RECOMMENDATION_WORKFLOW_STATUS } from "../constants/recommendationWorkflowStatus";
import { log } from "../telemetry/logger";

const useStyles = makeStyles({
  container: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalL,
    padding: tokens.spacingHorizontalXL,
  },
  card: {
    padding: tokens.spacingHorizontalL,
  },
  section: {
    marginTop: tokens.spacingVerticalL,
  },
  selectionGrid: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalM,
  },
  compareLayout: {
    display: "grid",
    gridTemplateColumns: "minmax(0, 1.2fr) minmax(0, 1fr)",
    gap: tokens.spacingHorizontalL,
    "@media (max-width: 1200px)": {
      gridTemplateColumns: "1fr",
    },
  },
  recRow: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalS,
    padding: tokens.spacingVerticalXS,
    borderRadius: tokens.borderRadiusMedium,
  },
  stickyPanel: {
    position: "sticky",
    top: tokens.spacingVerticalM,
    alignSelf: "start",
  },
  compromiseCard: {
    padding: tokens.spacingHorizontalM,
    marginBottom: tokens.spacingVerticalM,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  resultHeader: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalM,
    marginBottom: tokens.spacingVerticalM,
  },
  gaugeRow: {
    display: "flex",
    gap: tokens.spacingHorizontalL,
    flexWrap: "wrap" as const,
    marginTop: tokens.spacingVerticalM,
  },
  gauge: {
    display: "flex",
    flexDirection: "column" as const,
    alignItems: "center",
    minWidth: "100px",
  },
  gaugeRing: {
    width: "80px",
    height: "80px",
    borderRadius: "50%",
    border: "6px solid",
    display: "flex",
    alignItems: "center",
    justifyContent: "center",
  },
  tableCard: {
    padding: tokens.spacingHorizontalM,
  },
  swotGrid: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(240px, 1fr))",
    gap: tokens.spacingHorizontalM,
    marginTop: tokens.spacingVerticalM,
  },
  swotCard: {
    padding: tokens.spacingHorizontalM,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  swotList: {
    margin: 0,
    paddingLeft: tokens.spacingHorizontalM,
    color: tokens.colorNeutralForeground2,
  },
  sourceHint: {
    color: tokens.colorNeutralForeground3,
  },
  decisionMatrix: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
    gap: tokens.spacingHorizontalM,
    marginTop: tokens.spacingVerticalM,
  },
});

const PILLAR_OPTIONS = [
  "",
  "Security",
  "Cost",
  "Reliability",
  "Performance",
  "Operations",
];

export function GovernanceNegotiationPage() {
  const styles = useStyles();
  const { accessToken } = useAccessToken();
  const notify = useNotify();
  const [recommendations, setRecommendations] = useState<Recommendation[]>([]);
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [priorityPillar, setPriorityPillar] = useState("");
  const [nlInput, setNlInput] = useState("");
  const [result, setResult] = useState<
    GovernanceNegotiationResult | undefined
  >();
  const [loadingRecs, setLoadingRecs] = useState(true);
  const [negotiating, setNegotiating] = useState(false);
  const [error, setError] = useState<string | undefined>();
  const [searchParams] = useSearchParams();

  useEffect(() => {
    document.title = "NimbusIQ — Governance Negotiation";
  }, []);

  useEffect(() => {
    const correlationId = crypto.randomUUID();
    controlPlaneApi
      .listRecommendations(
        {
          status: `${RECOMMENDATION_WORKFLOW_STATUS.pending},${RECOMMENDATION_WORKFLOW_STATUS.pendingApproval},${RECOMMENDATION_WORKFLOW_STATUS.manualReview}`,
          orderBy: "riskweighted",
          limit: 100,
        },
        accessToken,
        correlationId,
      )
      .then((data) => {
        setRecommendations(data.value ?? []);
      })
      .catch((err: unknown) => {
        const message = err instanceof Error ? err.message : String(err);
        log.error("Failed to load recommendations:", { correlationId });
        setError(message);
      })
      .finally(() => setLoadingRecs(false));
  }, [accessToken]);

  useEffect(() => {
    const first = searchParams.get("first");
    const second = searchParams.get("second");

    if (!first || !second || first === second) {
      return;
    }

    const available = new Set(recommendations.map((r) => r.id));
    if (available.has(first) && available.has(second)) {
      setSelectedIds(new Set([first, second]));
    }
  }, [recommendations, searchParams]);

  const toggleSelection = useCallback((id: string) => {
    setSelectedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) {
        next.delete(id);
      } else {
        if (next.size >= 2) {
          return prev;
        }
        next.add(id);
      }
      return next;
    });
  }, []);

  const selectedRecommendations = recommendations.filter((recommendation) =>
    selectedIds.has(recommendation.id),
  );

  const selectedById = new Map(
    selectedRecommendations.map((recommendation) => [
      recommendation.id,
      recommendation,
    ]),
  );

  const negotiate = useCallback(() => {
    if (selectedIds.size !== 2) {
      notify({
        title: "Select exactly 2 recommendations",
        body: "Trade-off negotiation compares two recommendations side by side.",
        intent: "warning",
      });
      return;
    }

    const selectedServiceGroupIds = Array.from(
      new Set(
        selectedRecommendations
          .map((recommendation) => recommendation.serviceGroupId)
          .filter((id): id is string => Boolean(id)),
      ),
    );

    if (selectedServiceGroupIds.length !== 1) {
      notify({
        title: "Select recommendations from one service group",
        body: "Governance negotiation requires all selected recommendations to belong to the same service group.",
        intent: "warning",
      });
      return;
    }

    const serviceGroupId = selectedServiceGroupIds[0];
    const correlationId = crypto.randomUUID();
    setNegotiating(true);
    setResult(undefined);
    setError(undefined);

    controlPlaneApi
      .negotiateGovernance(
        {
          serviceGroupId,
          conflictIds: Array.from(selectedIds),
          preferences: {
            ...(priorityPillar ? { priorityPillar } : {}),
            ...(nlInput.trim()
              ? { naturalLanguageContext: nlInput.trim() }
              : {}),
          },
        },
        accessToken,
        correlationId,
      )
      .then(setResult)
      .catch((err: unknown) => {
        const message = err instanceof Error ? err.message : String(err);
        log.error("Negotiation failed:", { correlationId });
        setError(message);
        notify({ title: "Negotiation failed", body: message, intent: "error" });
      })
      .finally(() => setNegotiating(false));
  }, [selectedIds, priorityPillar, accessToken, notify, nlInput]);

  if (loadingRecs) {
    return (
      <div
        style={{ display: "flex", justifyContent: "center", padding: "50px" }}
      >
        <Spinner label="Loading recommendations..." />
      </div>
    );
  }

  return (
    <div className={styles.container}>
      <Text size={900} weight="bold">
        Governance Negotiation
      </Text>
      <Text size={300} style={{ color: tokens.colorNeutralForeground3 }}>
        Select competing recommendations and negotiate compromises across WAF
        pillars.
      </Text>

      <Card className={styles.card}>
        <Text size={500} weight="semibold">
          Compare Recommendations
        </Text>
        {recommendations.length === 0 ? (
          <Text
            size={300}
            style={{
              color: tokens.colorNeutralForeground3,
              marginTop: tokens.spacingVerticalM,
            }}
          >
            No pending recommendations available for negotiation. Run an
            analysis first.
          </Text>
        ) : (
          <div
            className={styles.compareLayout}
            style={{ marginTop: tokens.spacingVerticalM }}
          >
            <Card className={styles.tableCard}>
              <Text size={400} weight="semibold" block>
                Candidate recommendations
              </Text>
              <Table
                size="small"
                style={{ marginTop: tokens.spacingVerticalS }}
              >
                <TableHeader>
                  <TableRow>
                    <TableHeaderCell>Recommendation</TableHeaderCell>
                    <TableHeaderCell>Pillar</TableHeaderCell>
                    <TableHeaderCell>Priority</TableHeaderCell>
                    <TableHeaderCell>Confidence</TableHeaderCell>
                    <TableHeaderCell>Action</TableHeaderCell>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {recommendations.map((rec) => {
                    const selected = selectedIds.has(rec.id);
                    const disableSelect = !selected && selectedIds.size >= 2;
                    return (
                      <TableRow key={rec.id}>
                        <TableCell>
                          <Text size={300}>
                            {rec.title ?? rec.description ?? rec.resourceId}
                          </Text>
                        </TableCell>
                        <TableCell>
                          <Badge appearance="outline" color="informative">
                            {rec.category ?? "General"}
                          </Badge>
                        </TableCell>
                        <TableCell>
                          <Badge
                            appearance="tint"
                            color={
                              rec.priority === "critical"
                                ? "danger"
                                : rec.priority === "high"
                                  ? "warning"
                                  : rec.priority === "medium"
                                    ? "brand"
                                    : "informative"
                            }
                          >
                            {rec.priority}
                          </Badge>
                        </TableCell>
                        <TableCell>
                          <Text size={200}>
                            {Math.round((rec.confidenceScore ?? 0) * 100)}%
                          </Text>
                        </TableCell>
                        <TableCell>
                          <Button
                            appearance={selected ? "secondary" : "primary"}
                            size="small"
                            icon={
                              selected ? (
                                <Dismiss20Regular />
                              ) : (
                                <ArrowRight20Regular />
                              )
                            }
                            onClick={() => toggleSelection(rec.id)}
                            disabled={disableSelect}
                          >
                            {selected ? "Remove" : "Compare"}
                          </Button>
                        </TableCell>
                      </TableRow>
                    );
                  })}
                </TableBody>
              </Table>
            </Card>

            <Card className={`${styles.tableCard} ${styles.stickyPanel}`}>
              <Text size={400} weight="semibold" block>
                Side-by-side selection
              </Text>
              <Table
                size="small"
                style={{ marginTop: tokens.spacingVerticalS }}
              >
                <TableHeader>
                  <TableRow>
                    <TableHeaderCell>Slot</TableHeaderCell>
                    <TableHeaderCell>Recommendation</TableHeaderCell>
                    <TableHeaderCell>Pillar</TableHeaderCell>
                    <TableHeaderCell>Risk</TableHeaderCell>
                    <TableHeaderCell>Queue</TableHeaderCell>
                  </TableRow>
                </TableHeader>
                <TableBody>
                  {[0, 1].map((slot) => {
                    const rec = selectedRecommendations[slot];
                    return (
                      <TableRow key={`slot-${slot}`}>
                        <TableCell>
                          <Badge appearance="outline" color="brand">
                            Option {slot + 1}
                          </Badge>
                        </TableCell>
                        <TableCell>
                          <Text size={300}>
                            {rec
                              ? (rec.title ?? rec.description ?? rec.resourceId)
                              : "Select recommendation"}
                          </Text>
                        </TableCell>
                        <TableCell>
                          {rec ? (
                            <Badge appearance="outline" color="informative">
                              {rec.category ?? "General"}
                            </Badge>
                          ) : (
                            <Text size={200}>-</Text>
                          )}
                        </TableCell>
                        <TableCell>
                          <Text size={200}>
                            {rec
                              ? `${Math.round((rec.riskScore ?? 0) * 100)}%`
                              : "-"}
                          </Text>
                        </TableCell>
                        <TableCell>
                          <Text size={200}>
                            {rec
                              ? `${Math.round((rec.riskWeightedScore ?? 0) * 100)}%`
                              : "-"}
                          </Text>
                        </TableCell>
                      </TableRow>
                    );
                  })}
                </TableBody>
              </Table>
              <Text
                size={200}
                className={styles.sourceHint}
                style={{ marginTop: tokens.spacingVerticalS }}
              >
                Select exactly two recommendations from the same service group.
              </Text>
            </Card>
          </div>
        )}

        <div style={{ marginTop: tokens.spacingVerticalL }}>
          <Text
            size={300}
            weight="semibold"
            style={{ display: "block", marginBottom: tokens.spacingVerticalXS }}
          >
            Describe your priorities (optional)
          </Text>
          <Textarea
            placeholder="e.g., 'Prioritize cost savings but keep security at critical level. We can accept slightly lower performance during off-peak hours.'"
            value={nlInput}
            onChange={(_e, data) => setNlInput(data.value)}
            resize="vertical"
            style={{ width: "100%", minHeight: "80px" }}
          />
        </div>

        <div
          style={{
            display: "flex",
            gap: tokens.spacingHorizontalM,
            alignItems: "center",
            marginTop: tokens.spacingVerticalM,
          }}
        >
          <Text size={300}>Priority pillar:</Text>
          <Select
            value={priorityPillar}
            onChange={(_e, data) => setPriorityPillar(data.value)}
          >
            {PILLAR_OPTIONS.map((p) => (
              <option key={p} value={p}>
                {p || "(none)"}
              </option>
            ))}
          </Select>
          <Button
            appearance="primary"
            icon={<Gavel20Regular />}
            onClick={negotiate}
            disabled={negotiating || selectedIds.size !== 2}
          >
            {negotiating ? "Negotiating..." : "Negotiate"}
          </Button>
        </div>
      </Card>

      {error && (
        <Card className={styles.card}>
          <div
            style={{
              display: "flex",
              alignItems: "center",
              gap: tokens.spacingHorizontalS,
            }}
          >
            <ErrorCircle20Regular
              style={{ color: tokens.colorPaletteRedForeground1 }}
            />
            <Text weight="semibold">Error</Text>
          </div>
          <Text size={300} style={{ color: tokens.colorNeutralForeground3 }}>
            {error}
          </Text>
        </Card>
      )}

      {result && (
        <Card className={styles.card}>
          <div className={styles.resultHeader}>
            <Checkmark20Regular
              style={{ color: tokens.colorPaletteGreenForeground1 }}
            />
            <Text size={500} weight="semibold">
              Negotiation Result
            </Text>
            <Badge
              appearance="filled"
              color={
                result.resolution === "compromise_reached"
                  ? "warning"
                  : "success"
              }
            >
              {result.resolution === "compromise_reached"
                ? "Compromise"
                : "No Conflict"}
            </Badge>
            <Badge appearance="outline" color="informative">
              Confidence: {(result.confidence * 100).toFixed(0)}%
            </Badge>
            {result.agentReasoningSource && (
              <Badge appearance="outline" color="brand">
                {result.agentReasoningSource === "ai_foundry"
                  ? "MAF Agent Reasoning"
                  : "Rule-based Reasoning"}
              </Badge>
            )}
          </div>

          <Text
            size={300}
            style={{
              color: tokens.colorNeutralForeground3,
              marginBottom: tokens.spacingVerticalM,
            }}
          >
            {result.reasoning}
          </Text>

          <div className={styles.decisionMatrix}>
            {selectedRecommendations.map((recommendation, index) => {
              const compromise = result.compromises.find(
                (item) => item.recommendationId === recommendation.id,
              );
              const isRecommended =
                compromise?.adjustedRecommendation ===
                  compromise?.originalRecommendation ||
                (typeof compromise?.impactScore === "number" &&
                  compromise.impactScore >= result.confidence);

              return (
                <div key={recommendation.id} className={styles.swotCard}>
                  <Text weight="semibold">Option {index + 1}</Text>
                  <Text>
                    {recommendation.title ?? recommendation.recommendationType}
                  </Text>
                  <Text size={200}>
                    Optimizes for {recommendation.category ?? "platform"} via {recommendation.actionType}.
                  </Text>
                  <Text size={200}>
                    Risk {Math.round((recommendation.riskScore ?? 0) * 100)}% • Queue {Math.round((recommendation.riskWeightedScore ?? 0) * 100)}%
                  </Text>
                  <Text size={200}>
                    Approvals {recommendation.receivedApprovals}/{recommendation.requiredApprovals}
                  </Text>
                  <Badge
                    appearance="filled"
                    color={isRecommended ? "success" : "warning"}
                    style={{ marginTop: tokens.spacingVerticalXS }}
                  >
                    {isRecommended ? "Recommended path" : "Trade-off path"}
                  </Badge>
                  {compromise?.tradeoff && <Text size={200}>{compromise.tradeoff}</Text>}
                </div>
              );
            })}

            <div className={styles.swotCard}>
              <Text weight="semibold">Decision rationale</Text>
              <Text size={200}>{result.reasoning}</Text>
              <Text size={200}>
                Required approvals remain enforced for the selected recommendation before any remediation is executed.
              </Text>
            </div>
          </div>

          {result.compromises.length > 0 && (
            <div className={styles.gaugeRow}>
              {result.compromises.map((c, i) => {
                const score =
                  typeof c.impactScore === "number"
                    ? c.impactScore * 100
                    : result.confidence * 100;
                const color =
                  score >= 70
                    ? tokens.colorPaletteGreenBorder1
                    : score >= 40
                      ? tokens.colorPaletteYellowBorder1
                      : tokens.colorPaletteRedBorder1;
                return (
                  <div key={`gauge-${i}`} className={styles.gauge}>
                    <div
                      className={styles.gaugeRing}
                      style={{ borderColor: color }}
                    >
                      <Text weight="bold" size={400}>
                        {Math.round(score)}
                      </Text>
                    </div>
                    <Text
                      size={200}
                      style={{
                        marginTop: tokens.spacingVerticalXS,
                        textAlign: "center",
                      }}
                    >
                      {c.pillar ??
                        selectedById.get(c.recommendationId ?? "")?.category ??
                        `Option ${i + 1}`}
                    </Text>
                  </div>
                );
              })}
              <div className={styles.gauge}>
                <div
                  className={styles.gaugeRing}
                  style={{
                    borderColor:
                      result.confidence >= 0.7
                        ? tokens.colorPaletteGreenBorder1
                        : result.confidence >= 0.4
                          ? tokens.colorPaletteYellowBorder1
                          : tokens.colorPaletteRedBorder1,
                  }}
                >
                  <Text weight="bold" size={400}>
                    {(result.confidence * 100).toFixed(0)}%
                  </Text>
                </div>
                <Text
                  size={200}
                  style={{ marginTop: tokens.spacingVerticalXS }}
                >
                  Overall
                </Text>
              </div>
            </div>
          )}

          <div className={styles.section}>
            <Text size={400} weight="semibold">
              Compromises
            </Text>
            {result.compromises.map((c, i) => (
              <div key={i} className={styles.compromiseCard}>
                <div
                  style={{
                    display: "flex",
                    flexDirection: "column",
                    gap: tokens.spacingVerticalXS,
                  }}
                >
                  <Text size={300} weight="semibold">
                    Original: {c.originalRecommendation}
                  </Text>
                  {c.adjustedRecommendation !== c.originalRecommendation && (
                    <Text
                      size={300}
                      style={{ color: tokens.colorPaletteBlueForeground2 }}
                    >
                      Adjusted: {c.adjustedRecommendation}
                    </Text>
                  )}
                  <Text
                    size={200}
                    style={{ color: tokens.colorNeutralForeground3 }}
                  >
                    {c.tradeoff}
                  </Text>

                  {c.swot && (
                    <div className={styles.swotGrid}>
                      <div className={styles.swotCard}>
                        <Text weight="semibold" size={200}>
                          Strengths
                        </Text>
                        <ul className={styles.swotList}>
                          {c.swot.strengths.map((item, idx) => (
                            <li key={`s-${idx}`}>{item}</li>
                          ))}
                        </ul>
                      </div>
                      <div className={styles.swotCard}>
                        <Text weight="semibold" size={200}>
                          Weaknesses
                        </Text>
                        <ul className={styles.swotList}>
                          {c.swot.weaknesses.map((item, idx) => (
                            <li key={`w-${idx}`}>{item}</li>
                          ))}
                        </ul>
                      </div>
                      <div className={styles.swotCard}>
                        <Text weight="semibold" size={200}>
                          Opportunities
                        </Text>
                        <ul className={styles.swotList}>
                          {c.swot.opportunities.map((item, idx) => (
                            <li key={`o-${idx}`}>{item}</li>
                          ))}
                        </ul>
                      </div>
                      <div className={styles.swotCard}>
                        <Text weight="semibold" size={200}>
                          Threats
                        </Text>
                        <ul className={styles.swotList}>
                          {c.swot.threats.map((item, idx) => (
                            <li key={`t-${idx}`}>{item}</li>
                          ))}
                        </ul>
                      </div>
                    </div>
                  )}
                </div>
              </div>
            ))}
          </div>
        </Card>
      )}
    </div>
  );
}
