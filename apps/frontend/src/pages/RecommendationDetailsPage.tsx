import { useCallback, useEffect, useMemo, useState } from "react";
import { useParams, Link } from "react-router-dom";
import {
  Badge,
  Button,
  Card,
  Field,
  Input,
  Spinner,
  Text,
  makeStyles,
  tokens,
} from "@fluentui/react-components";
import DOMPurify from "isomorphic-dompurify";
import { controlPlaneApi } from "../services/controlPlaneApi";
import { useAccessToken } from "../auth/useAccessToken";
import { RecommendationDecisionPanel } from "../components/RecommendationDecisionPanel";
import { AuditTrail } from "../components/AuditTrail";
import { config } from "../config";

const useStyles = makeStyles({
  header: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalS,
    marginBottom: tokens.spacingVerticalL,
  },
  grid: {
    display: "grid",
    gridTemplateColumns: "2fr 1fr",
    gap: tokens.spacingHorizontalL,
    "@media (max-width: 900px)": {
      gridTemplateColumns: "1fr",
    },
  },
  stack: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalM,
  },
  subtle: {
    color: tokens.colorNeutralForeground3,
  },
  backLink: {
    color: tokens.colorBrandForeground1,
    textDecorationLine: "none",
  },
  badgeRow: {
    display: "flex",
    gap: tokens.spacingHorizontalS,
    flexWrap: "wrap",
  },
  impactGrid: {
    display: "grid",
    gridTemplateColumns: "1fr 1fr",
    gap: tokens.spacingHorizontalS,
  },
  summaryGrid: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
    gap: tokens.spacingHorizontalM,
  },
  summaryCell: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalXS,
  },
  summaryValue: {
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },
  badgeCluster: {
    display: "flex",
    gap: tokens.spacingHorizontalXS,
    flexWrap: "wrap",
    marginTop: tokens.spacingVerticalS,
  },
  listBlock: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalXS,
  },
  workflowList: {
    listStyleType: "none",
    margin: 0,
    padding: 0,
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalXS,
  },
  workflowItem: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "center",
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
  },
  actionRow: {
    display: "flex",
    flexWrap: "wrap",
    gap: tokens.spacingHorizontalS,
    marginTop: tokens.spacingVerticalS,
  },
  // ─── IaC preview styles ──────────────────────────────────────────────────────
  iacToolbar: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "center",
    flexWrap: "wrap",
    gap: tokens.spacingHorizontalS,
  },
  iacBadgeRow: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalXS,
  },
  iacCode: {
    fontFamily: tokens.fontFamilyMonospace,
    fontSize: tokens.fontSizeBase100,
    lineHeight: "1.6",
    backgroundColor: tokens.colorNeutralBackground2,
    borderRadius: tokens.borderRadiusMedium,
    padding: tokens.spacingVerticalM,
    overflowX: "auto",
    overflowY: "auto",
    maxHeight: "340px",
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    color: tokens.colorNeutralForeground2,
    margin: 0,
    whiteSpace: "pre",
  },
  iacEmpty: {
    padding: `${tokens.spacingVerticalL} 0`,
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
});

import {
  type Recommendation,
  type ChangeSetDetail,
  type ValueRealizationResult,
  type ConfidenceExplainer,
  type PolicyImpactSimulationResult,
  type GuardrailLintResult,
  type PullRequestResult,
  type RecommendationTaskResult,
  type RecommendationIacExamples,
  type RecommendationWorkflowStatus,
  type BlastRadiusResponse,
  type RecommendationLineageResponse,
  type RecommendationGroundingProvenance,
  ApiRequestError,
} from "../services/controlPlaneApi";
import {
  CHANGE_SET_ELIGIBLE_STATUSES,
  RECOMMENDATION_WORKFLOW_STATUS,
  normalizeRecommendationStatus,
} from "../constants/recommendationWorkflowStatus";

function getStatusGuidance(status: string | undefined): string {
  const normalized = normalizeRecommendationStatus(status);
  if (CHANGE_SET_ELIGIBLE_STATUSES.has(normalized)) {
    return "Eligible for simulation and change set generation.";
  }

  if (normalized === RECOMMENDATION_WORKFLOW_STATUS.pending) {
    return "Move this recommendation to Manual Review or Approved to enable simulation and change set generation.";
  }

  return "This recommendation status is not eligible for simulation or change set generation.";
}

function formatApiError(error: unknown, fallback: string): string {
  if (error instanceof ApiRequestError) {
    if (
      error.errorCode === "InvalidRecommendationStatus" &&
      error.details &&
      typeof error.details === "object"
    ) {
      const payload = error.details as { detail?: string };
      return (
        payload.detail ??
        "This action is not allowed for the current recommendation status."
      );
    }

    return error.message;
  }

  return error instanceof Error ? error.message : fallback;
}

function normalizeGroundingSourceLabel(source: string | undefined): string {
  if (!source) return "Seeded rule";

  switch (source.toLowerCase()) {
    case "learn_mcp":
      return "Learn MCP";
    case "seeded_rule":
      return "Seeded rule";
    default:
      return source
        .split("_")
        .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
        .join(" ");
  }
}

function resolveGroundingProvenance(
  recommendation: Recommendation,
  lineage: RecommendationLineageResponse | null,
): RecommendationGroundingProvenance | null {
  return recommendation.groundingProvenance ?? lineage?.provenance ?? null;
}

function formatGroundingTimestamp(timestamp: string | undefined): string {
  if (!timestamp) return "Not recorded";

  const parsed = new Date(timestamp);
  return Number.isNaN(parsed.getTime()) ? timestamp : parsed.toLocaleString();
}

function formatGroundingPercent(value: number | undefined): string {
  if (typeof value !== "number" || Number.isNaN(value)) return "Not scored";
  return `${Math.round(value * 100)}%`;
}

export function RecommendationDetailsPage() {
  const styles = useStyles();
  const { id } = useParams();
  const { accessToken } = useAccessToken();

  useEffect(() => {
    document.title = "NimbusIQ — Recommendation Details";
  }, []);

  const [data, setData] = useState<Recommendation | undefined>(undefined);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | undefined>(undefined);
  const [changeSet, setChangeSet] = useState<ChangeSetDetail | null>(null);
  const [changeSetLoading, setChangeSetLoading] = useState(false);
  const [changeSetError, setChangeSetError] = useState<string | undefined>(
    undefined,
  );
  const [validationBusy, setValidationBusy] = useState(false);
  const [valueRealization, setValueRealization] =
    useState<ValueRealizationResult | null>(null);
  const [valueLoading, setValueLoading] = useState(false);
  const [valueError, setValueError] = useState<string | undefined>(undefined);
  const [confidenceExplainer, setConfidenceExplainer] =
    useState<ConfidenceExplainer | null>(null);
  const [confidenceLoading, setConfidenceLoading] = useState(false);
  const [confidenceError, setConfidenceError] = useState<string | undefined>(
    undefined,
  );
  const [policySimulation, setPolicySimulation] =
    useState<PolicyImpactSimulationResult | null>(null);
  const [policyBusy, setPolicyBusy] = useState(false);
  const [policyError, setPolicyError] = useState<string | undefined>(undefined);
  const [guardrailLint, setGuardrailLint] =
    useState<GuardrailLintResult | null>(null);
  const [guardrailBusy, setGuardrailBusy] = useState(false);
  const [guardrailError, setGuardrailError] = useState<string | undefined>(
    undefined,
  );
  const [changeSetCreateBusy, setChangeSetCreateBusy] = useState(false);
  const [publishBusy, setPublishBusy] = useState(false);
  const [publishError, setPublishError] = useState<string | undefined>(
    undefined,
  );
  const [prBusy, setPrBusy] = useState(false);
  const [prError, setPrError] = useState<string | undefined>(undefined);
  const [pullRequest, setPullRequest] = useState<PullRequestResult | null>(
    null,
  );
  const [repoUrl, setRepoUrl] = useState(config.gitOps.repositoryUrl ?? "");
  const [targetBranch, setTargetBranch] = useState(config.gitOps.targetBranch);
  const [componentName, setComponentName] = useState(
    config.gitOps.componentName,
  );
  const [componentVersion, setComponentVersion] = useState(
    config.gitOps.componentVersion,
  );
  const [taskProvider, setTaskProvider] = useState("azure-devops");
  const [taskAssignee, setTaskAssignee] = useState("");
  const [taskDueDate, setTaskDueDate] = useState(
    new Date(Date.now() + 7 * 24 * 60 * 60 * 1000).toISOString().slice(0, 10),
  );
  const [taskNotes, setTaskNotes] = useState("");
  const [taskBusy, setTaskBusy] = useState(false);
  const [taskError, setTaskError] = useState<string | undefined>(undefined);
  const [taskResult, setTaskResult] = useState<RecommendationTaskResult | null>(
    null,
  );
  const [workflowStatus, setWorkflowStatus] =
    useState<RecommendationWorkflowStatus | null>(null);
  const [blastRadius, setBlastRadius] = useState<BlastRadiusResponse | null>(
    null,
  );
  const [blastRadiusLoading, setBlastRadiusLoading] = useState(false);
  const [blastRadiusError, setBlastRadiusError] = useState<string | undefined>(
    undefined,
  );
  const [lineage, setLineage] = useState<RecommendationLineageResponse | null>(
    null,
  );
  const [lineageLoading, setLineageLoading] = useState(false);
  const [lineageError, setLineageError] = useState<string | undefined>(
    undefined,
  );
  const [iacCopied, setIacCopied] = useState(false);
  const [iacExamples, setIacExamples] =
    useState<RecommendationIacExamples | null>(null);
  const [iacExamplesLoading, setIacExamplesLoading] = useState(false);
  const [iacExamplesError, setIacExamplesError] = useState<string | undefined>(
    undefined,
  );

  const normalizedRecommendationStatus = useMemo(
    () => normalizeRecommendationStatus(data?.status),
    [data?.status],
  );
  const isChangeSetEligible = useMemo(
    () => CHANGE_SET_ELIGIBLE_STATUSES.has(normalizedRecommendationStatus),
    [normalizedRecommendationStatus],
  );
  const statusGuidance = useMemo(
    () => getStatusGuidance(data?.status),
    [data?.status],
  );
  const impactSummary = useMemo(
    () => deriveImpactSummary(data?.estimatedImpact, data?.riskProfile),
    [data?.estimatedImpact, data?.riskProfile],
  );
  const readinessSummary = useMemo(
    () =>
      deriveReadinessSummary({
        recommendationStatus: data?.status,
        workflowStatus,
        changeSet,
        pullRequest,
      }),
    [changeSet, data?.status, pullRequest, workflowStatus],
  );
  const groundingProvenance = useMemo(
    () => (data ? resolveGroundingProvenance(data, lineage) : null),
    [data, lineage],
  );

  const load = useCallback(async () => {
    if (!id) return;
    setLoading(true);
    setError(undefined);
    try {
      const result = await controlPlaneApi.getRecommendation(id, accessToken);
      setData(result);
      try {
        const workflow = await controlPlaneApi.getRecommendationWorkflowStatus(
          id,
          accessToken,
        );
        setWorkflowStatus(workflow);
      } catch {
        setWorkflowStatus(null);
      }
    } catch (e) {
      setError(e instanceof Error ? e.message : String(e));
    } finally {
      setLoading(false);
    }
  }, [accessToken, id]);

  const loadChangeSet = useCallback(async () => {
    if (!id) return;
    setChangeSetLoading(true);
    setChangeSetError(undefined);
    try {
      const list = await controlPlaneApi.listChangeSets(id, accessToken);
      const latest = [...(list.value ?? [])].sort(
        (a, b) =>
          new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime(),
      )[0];

      if (!latest) {
        setChangeSet(null);
        return;
      }

      const detail = await controlPlaneApi.getChangeSet(latest.id, accessToken);
      setChangeSet(detail);
    } catch (e) {
      setChangeSetError(e instanceof Error ? e.message : String(e));
    } finally {
      setChangeSetLoading(false);
    }
  }, [accessToken, id]);

  const loadValueRealization = useCallback(async () => {
    if (!changeSet?.id) return;
    setValueLoading(true);
    setValueError(undefined);
    try {
      const result = await controlPlaneApi.getValueRealization(
        changeSet.id,
        accessToken,
      );
      setValueRealization(result);
    } catch (e) {
      setValueError(e instanceof Error ? e.message : String(e));
    } finally {
      setValueLoading(false);
    }
  }, [accessToken, changeSet?.id]);

  const runPreflight = useCallback(async () => {
    if (!changeSet?.id) return;
    setValidationBusy(true);
    try {
      await controlPlaneApi.validateChangeSet(changeSet.id, accessToken);
      await loadChangeSet();
      await loadValueRealization();
    } catch (e) {
      setChangeSetError(e instanceof Error ? e.message : String(e));
    } finally {
      setValidationBusy(false);
    }
  }, [accessToken, changeSet?.id, loadChangeSet, loadValueRealization]);

  const loadConfidenceExplainer = useCallback(async () => {
    if (!id) return;
    setConfidenceLoading(true);
    setConfidenceError(undefined);
    try {
      const result = await controlPlaneApi.getRecommendationConfidenceExplainer(
        id,
        accessToken,
      );
      setConfidenceExplainer(result);
    } catch (e) {
      setConfidenceError(e instanceof Error ? e.message : String(e));
    } finally {
      setConfidenceLoading(false);
    }
  }, [accessToken, id]);

  const loadWorkflowStatus = useCallback(async () => {
    if (!id) return;
    try {
      const result = await controlPlaneApi.getRecommendationWorkflowStatus(
        id,
        accessToken,
      );
      setWorkflowStatus(result);
    } catch {
      setWorkflowStatus(null);
    }
  }, [accessToken, id]);

  const loadBlastRadius = useCallback(async () => {
    if (!id || !data?.serviceGroupId) return;
    setBlastRadiusLoading(true);
    setBlastRadiusError(undefined);
    try {
      const result = await controlPlaneApi.getBlastRadius(
        data.serviceGroupId,
        { recommendationId: id },
        accessToken,
      );
      setBlastRadius(result);
    } catch (e) {
      setBlastRadiusError(e instanceof Error ? e.message : String(e));
    } finally {
      setBlastRadiusLoading(false);
    }
  }, [accessToken, data?.serviceGroupId, id]);

  const loadLineage = useCallback(async () => {
    if (!id) return;
    setLineageLoading(true);
    setLineageError(undefined);
    try {
      const result = await controlPlaneApi.getRecommendationLineage(
        id,
        accessToken,
      );
      setLineage(result);
    } catch (e) {
      setLineageError(e instanceof Error ? e.message : String(e));
    } finally {
      setLineageLoading(false);
    }
  }, [accessToken, id]);

  const handleCopyIac = useCallback(() => {
    if (!changeSet?.content) return;
    void navigator.clipboard.writeText(changeSet.content);
    setIacCopied(true);
    setTimeout(() => setIacCopied(false), 2000);
  }, [changeSet?.content]);

  const runPolicySimulation = useCallback(async () => {
    if (!id) return;
    if (!isChangeSetEligible) {
      setPolicyError(statusGuidance);
      return;
    }
    setPolicyBusy(true);
    setPolicyError(undefined);
    try {
      const result = await controlPlaneApi.simulateRecommendationPolicyImpact(
        id,
        { policyThreshold: 60 },
        accessToken,
      );
      setPolicySimulation(result);
    } catch (e) {
      setPolicyError(formatApiError(e, "Failed to run policy simulation."));
    } finally {
      setPolicyBusy(false);
    }
  }, [accessToken, id, isChangeSetEligible, statusGuidance]);

  const runGuardrailLint = useCallback(async () => {
    if (!changeSet?.id) return;
    setGuardrailBusy(true);
    setGuardrailError(undefined);
    try {
      const result = await controlPlaneApi.lintChangeSetGuardrails(
        changeSet.id,
        accessToken,
      );
      setGuardrailLint(result);
    } catch (e) {
      setGuardrailError(e instanceof Error ? e.message : String(e));
    } finally {
      setGuardrailBusy(false);
    }
  }, [accessToken, changeSet?.id]);

  const createChangeSet = useCallback(async () => {
    if (!id) return;
    if (!isChangeSetEligible) {
      setChangeSetError(statusGuidance);
      return;
    }
    setChangeSetCreateBusy(true);
    setChangeSetError(undefined);
    try {
      await controlPlaneApi.generateChangeSet(id, "bicep", accessToken);
      await loadChangeSet();
    } catch (e) {
      setChangeSetError(formatApiError(e, "Failed to generate change set."));
    } finally {
      setChangeSetCreateBusy(false);
    }
  }, [accessToken, id, isChangeSetEligible, loadChangeSet, statusGuidance]);

  const publishChangeSet = useCallback(async () => {
    if (!changeSet?.id) return;
    setPublishBusy(true);
    setPublishError(undefined);
    try {
      await controlPlaneApi.publishChangeSet(
        changeSet.id,
        {
          releaseId: `release-${new Date().toISOString().slice(0, 10)}`,
          componentName,
          componentVersion,
          validationScopeId: "default",
        },
        accessToken,
      );
      await loadChangeSet();
      await loadValueRealization();
    } catch (e) {
      setPublishError(e instanceof Error ? e.message : String(e));
    } finally {
      setPublishBusy(false);
    }
  }, [
    accessToken,
    changeSet?.id,
    componentName,
    componentVersion,
    loadChangeSet,
    loadValueRealization,
  ]);

  const createPullRequest = useCallback(async () => {
    if (!id || !changeSet?.id) return;
    setPrBusy(true);
    setPrError(undefined);
    try {
      const pr = await controlPlaneApi.createRecommendationPullRequest(
        id,
        {
          changeSetId: changeSet.id,
          repositoryUrl: repoUrl,
          targetBranch,
          labels: ["nimbusiq", "automated-remediation"],
        },
        accessToken,
      );
      setPullRequest(pr);
    } catch (e) {
      setPrError(e instanceof Error ? e.message : String(e));
    } finally {
      setPrBusy(false);
    }
  }, [accessToken, changeSet?.id, id, repoUrl, targetBranch]);

  const createExecutionTask = useCallback(async () => {
    if (!id || !data) return;
    setTaskBusy(true);
    setTaskError(undefined);
    try {
      const result = await controlPlaneApi.createRecommendationTask(
        id,
        {
          provider: taskProvider,
          assignee: taskAssignee || undefined,
          dueDate: taskDueDate || undefined,
          notes: taskNotes || undefined,
          title: `[${String(data.priority ?? "medium").toUpperCase()}] ${data.title ?? data.recommendationType}`,
        },
        accessToken,
      );
      setTaskResult(result);
    } catch (e) {
      setTaskError(e instanceof Error ? e.message : String(e));
    } finally {
      setTaskBusy(false);
    }
  }, [
    accessToken,
    data,
    id,
    taskAssignee,
    taskDueDate,
    taskNotes,
    taskProvider,
  ]);

  const loadIacExamples = useCallback(async () => {
    if (!id) return;
    setIacExamplesLoading(true);
    setIacExamplesError(undefined);
    try {
      const result = await controlPlaneApi.getRecommendationIacExamples(
        id,
        accessToken,
      );
      setIacExamples(result);
    } catch (e) {
      setIacExamplesError(
        e instanceof Error ? e.message : "Failed to load remediation IaC output.",
      );
    } finally {
      setIacExamplesLoading(false);
    }
  }, [accessToken, id]);

  useEffect(() => {
    void load();
  }, [load]);

  useEffect(() => {
    void loadConfidenceExplainer();
  }, [loadConfidenceExplainer]);

  useEffect(() => {
    void loadChangeSet();
  }, [loadChangeSet]);

  useEffect(() => {
    void loadWorkflowStatus();
  }, [loadWorkflowStatus]);

  useEffect(() => {
    void loadLineage();
  }, [loadLineage]);

  useEffect(() => {
    void loadBlastRadius();
  }, [loadBlastRadius]);

  useEffect(() => {
    void loadValueRealization();
  }, [loadValueRealization]);

  if (!id) {
    return (
      <Card>
        <Text>Missing recommendation ID.</Text>
      </Card>
    );
  }

  return (
    <div>
      <div className={styles.header}>
        <Link className={styles.backLink} to="/recommendations">
          <Text size={300}>{"<"} Back to recommendations</Text>
        </Link>
        <Text size={800} weight="bold">
          Recommendation Details
        </Text>
        <Text className={styles.subtle} size={300}>
          Confidence, evidence, trade-offs, and approval lineage.
        </Text>
      </div>

      {loading && <Spinner label="Loading recommendation..." />}

      {!loading && error && (
        <Card>
          <Text weight="semibold">Failed to load recommendation</Text>
          <Text className={styles.subtle} size={300}>
            {error}
          </Text>
        </Card>
      )}

      {!loading && !error && data && (
        <div className={styles.grid}>
          <div className={styles.stack}>
            <Card>
              <Text size={600} weight="semibold">
                {data.title ?? data.recommendationType ?? "Recommendation"}
              </Text>
              <Text className={styles.subtle} size={300}>
                {data.actionType} • {data.status} •{" "}
                {(Number(data.confidenceScore ?? 0) * 100).toFixed(0)}%
                confidence
                {data.confidenceSource &&
                  ` • ${data.confidenceSource === "ai_foundry" ? "✨ AI-Enhanced" : "Basic analysis"}`}
              </Text>
              <Text style={{ marginTop: tokens.spacingVerticalM }}>
                {sanitizeText(data.description ?? "No description provided.")}
              </Text>
              <div className={styles.badgeRow}>
                <Badge appearance="outline">{resolveSourceLabel(data)}</Badge>
                {data.wellArchitectedPillar && (
                  <Badge appearance="outline">
                    {data.wellArchitectedPillar}
                  </Badge>
                )}
                <Badge appearance="tint" color="brand">
                  {readinessSummary.label}
                </Badge>
                <Badge
                  appearance="outline"
                  color={
                    data.confidenceSource === "ai_foundry"
                      ? "success"
                      : "informative"
                  }
                >
                  {data.confidenceSource === "ai_foundry"
                    ? "AI-assisted reasoning"
                    : "Seeded analysis"}
                </Badge>
              </div>
            </Card>

            <Card>
              <Text size={600} weight="semibold">
                Operational Summary
              </Text>
              <div className={styles.summaryGrid}>
                <div className={styles.summaryCell}>
                  <Text size={200} className={styles.subtle}>
                    Why this exists
                  </Text>
                  <Text className={styles.summaryValue}>
                    {sanitizeText(
                      data.rationale ??
                        data.description ??
                        "Recommendation evidence is still being summarized.",
                    )}
                  </Text>
                </div>
                <div className={styles.summaryCell}>
                  <Text size={200} className={styles.subtle}>
                    Source engine
                  </Text>
                  <Text className={styles.summaryValue}>
                    {resolveSourceLabel(data)}
                  </Text>
                </div>
                <div className={styles.summaryCell}>
                  <Text size={200} className={styles.subtle}>
                    Expected benefit
                  </Text>
                  <Text className={styles.summaryValue}>
                    {impactSummary.primaryBenefit}
                  </Text>
                </div>
                <div className={styles.summaryCell}>
                  <Text size={200} className={styles.subtle}>
                    Implementation effort
                  </Text>
                  <Text className={styles.summaryValue}>
                    {impactSummary.effort}
                  </Text>
                </div>
                <div className={styles.summaryCell}>
                  <Text size={200} className={styles.subtle}>
                    Affected workload
                  </Text>
                  <Text className={styles.summaryValue}>
                    {data.serviceGroupName ??
                      "Service group context unavailable"}
                  </Text>
                </div>
                <div className={styles.summaryCell}>
                  <Text size={200} className={styles.subtle}>
                    Blast radius
                  </Text>
                  <Text className={styles.summaryValue}>
                    {formatBlastRadiusSummary(blastRadius)}
                  </Text>
                </div>
              </div>
              <div className={styles.badgeCluster}>
                {impactSummary.badges.map((badge) => (
                  <Badge
                    key={badge.label}
                    appearance={badge.appearance}
                    color={badge.color}
                  >
                    {badge.label}
                  </Badge>
                ))}
              </div>
            </Card>

            <Card>
              <Text size={600} weight="semibold">
                MCP Grounding
              </Text>
              <Text size={200} className={styles.subtle}>
                {groundingProvenance?.groundingSource === "learn_mcp"
                  ? "This recommendation was enriched with Learn MCP evidence before it was presented here."
                  : "No Learn MCP grounding was recorded for this recommendation; the current detail view is based on seeded analysis and stored evidence."}
              </Text>
              <div
                className={styles.summaryGrid}
                style={{ marginTop: tokens.spacingVerticalM }}
              >
                <div className={styles.summaryCell}>
                  <Text size={200} className={styles.subtle}>
                    Grounding source
                  </Text>
                  <Text className={styles.summaryValue}>
                    {normalizeGroundingSourceLabel(
                      groundingProvenance?.groundingSource ??
                        data.groundingSource,
                    )}
                  </Text>
                </div>
                <div className={styles.summaryCell}>
                  <Text size={200} className={styles.subtle}>
                    Grounded at
                  </Text>
                  <Text className={styles.summaryValue}>
                    {formatGroundingTimestamp(
                      groundingProvenance?.groundingTimestampUtc ??
                        data.groundingTimestampUtc,
                    )}
                  </Text>
                </div>
                <div className={styles.summaryCell}>
                  <Text size={200} className={styles.subtle}>
                    Grounding quality
                  </Text>
                  <Text className={styles.summaryValue}>
                    {formatGroundingPercent(
                      groundingProvenance?.groundingQuality,
                    )}
                  </Text>
                </div>
                <div className={styles.summaryCell}>
                  <Text size={200} className={styles.subtle}>
                    Recency score
                  </Text>
                  <Text className={styles.summaryValue}>
                    {formatGroundingPercent(
                      groundingProvenance?.groundingRecencyScore,
                    )}
                  </Text>
                </div>
              </div>
              <div
                className={styles.listBlock}
                style={{ marginTop: tokens.spacingVerticalM }}
              >
                <Text size={200}>
                  <strong>Query:</strong>{" "}
                  {groundingProvenance?.groundingQuery?.trim() ||
                    "No grounding query recorded."}
                </Text>
                {groundingProvenance?.groundingToolRunId && (
                  <Text size={200} className={styles.subtle}>
                    Tool run: {groundingProvenance.groundingToolRunId}
                  </Text>
                )}
              </div>
            </Card>

            <TrustLensCard data={data} styles={styles} />

            <Card>
              <Text size={600} weight="semibold">
                Rationale
              </Text>
              <Text className={styles.subtle} size={300}>
                {sanitizeText(data.rationale ?? "No rationale provided.")}
              </Text>
            </Card>

            {data.triggerReason && (
              <Card>
                <Text size={600} weight="semibold">
                  Trigger
                </Text>
                <div className={styles.badgeRow}>
                  <Badge appearance="outline">
                    {formatTriggerReason(data.triggerReason)}
                  </Badge>
                </div>
              </Card>
            )}

            <EstimatedImpactCard json={data.estimatedImpact} styles={styles} />
            <TradeoffProfileCard json={data.tradeoffProfile} styles={styles} />
            <RiskProfileCard json={data.riskProfile} styles={styles} />

            {data.impactedServices && (
              <Card>
                <Text size={600} weight="semibold">
                  Impacted Services
                </Text>
                <ImpactedServicesDisplay json={data.impactedServices} />
              </Card>
            )}

            <Card>
              <Text size={600} weight="semibold">
                Why This Exists
              </Text>
              <Text className={styles.subtle} size={300}>
                {sanitizeText(data.rationale ?? "No rationale provided.")}
              </Text>
            </Card>

            <Card>
              <Text size={600} weight="semibold">
                Key Evidence
              </Text>
              <Text className={styles.subtle} size={300}>
                {formatEvidenceReferences(data.evidenceReferences)}
              </Text>
            </Card>

            <Card>
              <Text size={600} weight="semibold">
                Evidence & Lineage
              </Text>
              <Text size={200} className={styles.subtle}>
                Follow the path from evidence to recommendation to remediation.
              </Text>
              <div className={styles.listBlock}>
                <Text size={200}>
                  <strong>Evidence:</strong>{" "}
                  {formatEvidenceReferences(data.evidenceReferences)}
                </Text>
                {groundingProvenance && (
                  <Text size={200}>
                    <strong>Grounding:</strong>{" "}
                    {normalizeGroundingSourceLabel(
                      groundingProvenance.groundingSource,
                    )}
                    {groundingProvenance.groundingTimestampUtc
                      ? ` at ${formatGroundingTimestamp(groundingProvenance.groundingTimestampUtc)}`
                      : ""}
                  </Text>
                )}
                {data.changeContext && (
                  <div>
                    <Text size={200}>
                      <strong>Change Context:</strong>
                    </Text>
                    <ChangeContextDisplay json={data.changeContext} />
                  </div>
                )}
                {lineageLoading && (
                  <Spinner size="tiny" label="Loading lineage…" />
                )}
                {!lineageLoading &&
                  lineage?.steps?.map((step, index) => (
                    <Text
                      key={`${step.id ?? step.title ?? "step"}-${index}`}
                      size={200}
                    >
                      •{" "}
                      <strong>
                        {step.title ?? step.stage ?? `Step ${index + 1}`}
                      </strong>
                      {step.summary ? ` — ${step.summary}` : ""}
                      {typeof step.confidence === "number"
                        ? ` (${Math.round(step.confidence * 100)}% confidence)`
                        : ""}
                    </Text>
                  ))}
                {!lineageLoading && !lineage?.steps?.length && (
                  <Text size={200} className={styles.subtle}>
                    No lineage checkpoints recorded yet.
                  </Text>
                )}
                {lineageError && (
                  <Text
                    size={200}
                    style={{ color: tokens.colorPaletteRedForeground1 }}
                  >
                    {lineageError}
                  </Text>
                )}
              </div>
            </Card>

            <Card>
              <Text size={600} weight="semibold">
                Blast Radius
              </Text>
              {blastRadiusLoading && (
                <Spinner size="tiny" label="Loading blast radius…" />
              )}
              {!blastRadiusLoading && (
                <div className={styles.summaryGrid}>
                  <div className={styles.summaryCell}>
                    <Text size={200} className={styles.subtle}>
                      Affected resources
                    </Text>
                    <Text className={styles.summaryValue}>
                      {blastRadius?.resourceCount ?? 0}
                    </Text>
                  </div>
                  <div className={styles.summaryCell}>
                    <Text size={200} className={styles.subtle}>
                      Affected identities
                    </Text>
                    <Text className={styles.summaryValue}>
                      {blastRadius?.identityCount ?? 0}
                    </Text>
                  </div>
                  <div className={styles.summaryCell}>
                    <Text size={200} className={styles.subtle}>
                      Impacted services
                    </Text>
                    <Text className={styles.summaryValue}>
                      {formatImpactedServicesCount(data.impactedServices)}
                    </Text>
                  </div>
                  <div className={styles.summaryCell}>
                    <Text size={200} className={styles.subtle}>
                      Shared recommendations
                    </Text>
                    <Text className={styles.summaryValue}>
                      {blastRadius?.sharedRecommendations?.length ?? 0}
                    </Text>
                  </div>
                </div>
              )}
              {blastRadius?.affectedResources?.slice(0, 5).map((resource) => (
                <Text key={resource.resourceId} size={200}>
                  • {resource.name} ({resource.type}) — {resource.impactType}
                </Text>
              ))}
              {blastRadiusError && (
                <Text
                  size={200}
                  style={{ color: tokens.colorPaletteRedForeground1 }}
                >
                  {blastRadiusError}
                </Text>
              )}
            </Card>

            <Card>
              <Text size={600} weight="semibold">
                If You Approve This
              </Text>
              <Text size={300}>{readinessSummary.description}</Text>
              <div className={styles.badgeCluster}>
                {readinessSummary.badges.map((badge) => (
                  <Badge
                    key={badge.label}
                    appearance={badge.appearance}
                    color={badge.color}
                  >
                    {badge.label}
                  </Badge>
                ))}
              </div>
            </Card>

            {/* IaC Preview — surfaces generated Bicep/Terraform with one-click copy */}
            {(changeSet !== null || changeSetLoading) && (
              <Card>
                <div className={styles.iacToolbar}>
                  <div className={styles.iacBadgeRow}>
                    <Text size={600} weight="semibold">
                      IaC Preview
                    </Text>
                    {changeSet?.format && (
                      <Badge appearance="tint" color="brand">
                        {changeSet.format}
                      </Badge>
                    )}
                    {changeSet?.status && (
                      <Badge appearance="outline">{changeSet.status}</Badge>
                    )}
                  </div>
                  {changeSet?.content && (
                    <Button
                      appearance="subtle"
                      size="small"
                      onClick={handleCopyIac}
                      aria-label="Copy IaC content to clipboard"
                    >
                      {iacCopied ? "✓ Copied!" : "Copy"}
                    </Button>
                  )}
                </div>
                {changeSetLoading && (
                  <Spinner size="small" label="Loading change set…" />
                )}
                {!changeSetLoading && !changeSet?.content && (
                  <Text className={styles.iacEmpty}>
                    No IaC content yet. Generate a change set to preview the
                    template.
                  </Text>
                )}
                {!changeSetLoading && changeSet?.content && (
                  <pre
                    className={styles.iacCode}
                    aria-label="IaC template content"
                  >
                    {changeSet.content}
                  </pre>
                )}
              </Card>
            )}
          </div>

          <div className={styles.stack}>
            <ConfidenceExplainerCard
              data={confidenceExplainer}
              loading={confidenceLoading}
              error={confidenceError}
              onRefresh={loadConfidenceExplainer}
            />

            <PolicyImpactSimulatorCard
              result={policySimulation}
              busy={policyBusy}
              error={policyError}
              onSimulate={runPolicySimulation}
              canSimulate={isChangeSetEligible}
              statusGuidance={statusGuidance}
            />

            <ChangeSetValidationCard
              changeSet={changeSet}
              workflowStatus={workflowStatus}
              recommendationId={id}
              loading={changeSetLoading}
              error={changeSetError}
              avmExamples={iacExamples}
              avmExamplesLoading={iacExamplesLoading}
              avmExamplesError={iacExamplesError}
              onGenerateAvmExamples={loadIacExamples}
              onValidate={runPreflight}
              onCreateChangeSet={createChangeSet}
              onPublish={publishChangeSet}
              onCreatePullRequest={createPullRequest}
              busy={validationBusy}
              createBusy={changeSetCreateBusy}
              publishBusy={publishBusy}
              publishError={publishError}
              prBusy={prBusy}
              prError={prError}
              pullRequest={pullRequest}
              repoUrl={repoUrl}
              onRepoUrlChange={setRepoUrl}
              targetBranch={targetBranch}
              onTargetBranchChange={setTargetBranch}
              componentName={componentName}
              onComponentNameChange={setComponentName}
              componentVersion={componentVersion}
              onComponentVersionChange={setComponentVersion}
              guardrail={guardrailLint}
              guardrailBusy={guardrailBusy}
              guardrailError={guardrailError}
              onGuardrailLint={runGuardrailLint}
              recommendationStatus={data.status}
              canCreateChangeSet={isChangeSetEligible}
              statusGuidance={statusGuidance}
            />

            <ValueRealizationCard
              changeSet={changeSet}
              result={valueRealization}
              loading={valueLoading}
              error={valueError}
            />

            <AgentReplayPanel
              analysisRunId={data.analysisRunId}
              accessToken={accessToken}
            />

            <RecommendationDecisionPanel
              recommendationId={id}
              status={String(data.status ?? "")}
              priority={String(data.priority ?? "")}
              actionType={String(data.actionType ?? "")}
              resourceId={String(data.resourceId ?? "")}
              onChanged={load}
            />

            <Card>
              <Text size={600} weight="semibold">
                Execution Task
              </Text>
              <Text size={200} className={styles.subtle}>
                Create a tracked implementation task from this recommendation.
              </Text>
              <Field label="Task Provider">
                <Input
                  value={taskProvider}
                  onChange={(_, d) => setTaskProvider(d.value)}
                />
              </Field>
              <Field label="Assignee">
                <Input
                  value={taskAssignee}
                  onChange={(_, d) => setTaskAssignee(d.value)}
                  placeholder="user@company.com"
                />
              </Field>
              <Field label="Due Date">
                <Input
                  type="date"
                  value={taskDueDate}
                  onChange={(_, d) => setTaskDueDate(d.value)}
                />
              </Field>
              <Field label="Notes">
                <Input
                  value={taskNotes}
                  onChange={(_, d) => setTaskNotes(d.value)}
                  placeholder="Deployment window, dependencies, owner notes..."
                />
              </Field>
              <div className={styles.actionRow}>
                <Button
                  appearance="primary"
                  disabled={taskBusy}
                  onClick={() => void createExecutionTask()}
                >
                  {taskBusy ? "Creating…" : "Create Task"}
                </Button>
                {taskResult && (
                  <Button
                    appearance="secondary"
                    onClick={() => {
                      void navigator.clipboard.writeText(
                        JSON.stringify(taskResult.payload, null, 2),
                      );
                    }}
                  >
                    Copy Task Payload
                  </Button>
                )}
              </div>
              {taskResult && (
                <Text size={200} className={styles.subtle}>
                  Created {taskResult.taskId} ({taskResult.provider}) due{" "}
                  {taskResult.dueDate}.
                </Text>
              )}
              {taskError && (
                <Text
                  size={200}
                  style={{ color: tokens.colorPaletteRedForeground1 }}
                >
                  {taskError}
                </Text>
              )}
            </Card>

            <AuditTrail
              entityType="recommendation"
              entityId={id}
              accessToken={accessToken}
            />
          </div>
        </div>
      )}
    </div>
  );
}

function tryParseJson(
  json: string | undefined,
): Record<string, unknown> | null {
  if (!json) return null;
  try {
    return JSON.parse(json) as Record<string, unknown>;
  } catch {
    return null;
  }
}

function formatTriggerReason(reason: string): string {
  return reason.replace(/_/g, " ").replace(/\b\w/g, (c) => c.toUpperCase());
}

function resolveSourceLabel(data: Recommendation): string {
  if (data.sourceLabel) return data.sourceLabel;
  if (data.source) {
    return data.source
      .split("_")
      .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
      .join(" ");
  }
  if (data.confidenceSource === "ai_foundry") return "AI Synthesis";
  return "Unclassified";
}

// Fields that are internal/structural and shouldn't be rendered as display labels.
const CHANGE_CONTEXT_INTERNAL_KEYS = new Set([
  "ruleId",
  "source",
  "pillar",
  "affectedResources",
  "driftCategory",
  "grounding",
  "lineage",
  "currentFieldLabel",
  "currentValues",
  "requiredValue",
]);

function ChangeContextDisplay({ json }: { json: string }) {
  const parsed = useMemo(() => tryParseJson(json), [json]);
  if (!parsed) return null;

  const currentFieldLabel =
    typeof parsed.currentFieldLabel === "string"
      ? parsed.currentFieldLabel
      : null;
  const currentValues = Array.isArray(parsed.currentValues)
    ? (parsed.currentValues as unknown[]).map(String).filter(Boolean)
    : null;
  const requiredValue =
    typeof parsed.requiredValue === "string" ? parsed.requiredValue : null;

  const hasViolationDetail =
    (currentValues && currentValues.length > 0) || requiredValue;

  return (
    <div
      style={{ display: "flex", flexDirection: "column", gap: "4px" }}
    >
      {hasViolationDetail && (
        <div style={{ display: "flex", gap: "12px", flexWrap: "wrap" }}>
          {currentValues && currentValues.length > 0 && (
            <span>
              <Text size={200} weight="semibold">
                Current {currentFieldLabel ?? "value"}:{" "}
              </Text>
              <Text size={200}>{currentValues.join(", ")}</Text>
            </span>
          )}
          {requiredValue && (
            <span>
              <Text size={200} weight="semibold">
                Required:{" "}
              </Text>
              <Text size={200}>{requiredValue}</Text>
            </span>
          )}
        </div>
      )}
      <Text size={200}>
        {Object.entries(parsed)
          .filter(([k]) => !CHANGE_CONTEXT_INTERNAL_KEYS.has(k))
          .map(([k, v]) => `${k}: ${String(v)}`)
          .join(" • ")}
      </Text>
    </div>
  );
}

function EstimatedImpactCard({
  json,
  styles,
}: {
  json?: string;
  styles: ReturnType<typeof useStyles>;
}) {
  const parsed = useMemo(() => tryParseJson(json), [json]);
  if (!parsed) return null;

  const scoreImprovement =
    parsed.scoreImprovement ?? parsed.availabilityDelta ?? parsed.securityDelta;
  const monthlySavings =
    parsed.monthlySavings ??
    (typeof parsed.costDelta === "number" && parsed.costDelta !== 0
      ? parsed.costDelta
      : undefined);
  const implementationCost = parsed.implementationCost;
  const timeToImplement = parsed.timeToImplement;
  const performanceDelta = parsed.performanceDelta;

  return (
    <Card>
      <Text size={600} weight="semibold">
        Estimated Impact
      </Text>
      <div className={styles.impactGrid}>
        {scoreImprovement != null && (
          <div>
            <Text size={200} className={styles.subtle}>
              Score Improvement
            </Text>
            <br />
            <Text weight="semibold">
              {typeof scoreImprovement === "number"
                ? `+${scoreImprovement}`
                : `${String(scoreImprovement)}`}{" "}
              pts
            </Text>
          </div>
        )}
        {monthlySavings != null && (
          <div>
            <Text size={200} className={styles.subtle}>
              {typeof monthlySavings === "number" && monthlySavings < 0
                ? "Est. Monthly Cost"
                : "Monthly Savings"}
            </Text>
            <br />
            <Text weight="semibold">
              ${String(Math.abs(Number(monthlySavings)))}
            </Text>
          </div>
        )}
        {performanceDelta != null && Number(performanceDelta) !== 0 && (
          <div>
            <Text size={200} className={styles.subtle}>
              Performance Impact
            </Text>
            <br />
            <Text weight="semibold">
              {Number(performanceDelta) > 0 ? "+" : ""}
              {String(performanceDelta)} pts
            </Text>
          </div>
        )}
        {implementationCost != null && (
          <div>
            <Text size={200} className={styles.subtle}>
              Implementation Cost
            </Text>
            <br />
            <Text weight="semibold">{String(implementationCost)}</Text>
          </div>
        )}
        {timeToImplement != null && (
          <div>
            <Text size={200} className={styles.subtle}>
              Time to Implement
            </Text>
            <br />
            <Text weight="semibold">{String(timeToImplement)}</Text>
          </div>
        )}
      </div>
    </Card>
  );
}

function TradeoffProfileCard({
  json,
  styles,
}: {
  json?: string;
  styles: ReturnType<typeof useStyles>;
}) {
  const parsed = useMemo(() => tryParseJson(json), [json]);
  if (!parsed) return null;
  const improves = parsed.improves as string[] | undefined;
  const degrades = parsed.degrades as string[] | undefined;
  const neutral = parsed.neutral as string[] | undefined;
  return (
    <Card>
      <Text size={600} weight="semibold">
        Trade-off Profile
      </Text>
      <div className={styles.badgeRow}>
        {improves?.map((item) => (
          <Badge key={item} color="success" appearance="filled">
            {item}
          </Badge>
        ))}
        {degrades?.map((item) => (
          <Badge key={item} color="danger" appearance="filled">
            {item}
          </Badge>
        ))}
        {neutral?.map((item) => (
          <Badge key={item} color="informative" appearance="outline">
            {item}
          </Badge>
        ))}
      </div>
    </Card>
  );
}

function RiskProfileCard({
  json,
  styles,
}: {
  json?: string;
  styles: ReturnType<typeof useStyles>;
}) {
  const parsed = useMemo(() => tryParseJson(json), [json]);
  if (!parsed) return null;
  const level = String(parsed.currentRisk ?? parsed.level ?? "unknown");
  const badgeColor =
    level === "low" || level === "minimal"
      ? ("success" as const)
      : level === "medium"
        ? ("warning" as const)
        : ("danger" as const);
  return (
    <Card>
      <Text size={600} weight="semibold">
        Risk Profile
      </Text>
      <div className={styles.badgeRow}>
        <Badge color={badgeColor} appearance="filled">
          {level.toUpperCase()}
        </Badge>
        {parsed.riskCategory != null && (
          <Badge appearance="outline">{String(parsed.riskCategory)}</Badge>
        )}
        {parsed.residualRisk != null && (
          <Badge color="success" appearance="outline">
            Residual: {String(parsed.residualRisk)}
          </Badge>
        )}
        {parsed.rollbackComplexity != null && (
          <Badge appearance="outline">
            Rollback: {String(parsed.rollbackComplexity)}
          </Badge>
        )}
        {parsed.downtime === true && (
          <Badge color="danger" appearance="outline">
            Requires Downtime
          </Badge>
        )}
      </div>
      {parsed.mitigationSteps && (
        <Text size={200} className={styles.subtle}>
          Mitigation: {String(parsed.mitigationSteps)}
        </Text>
      )}
    </Card>
  );
}

function ImpactedServicesDisplay({ json }: { json: string }) {
  const parsed = useMemo(() => {
    try {
      return JSON.parse(json) as string[];
    } catch {
      return null;
    }
  }, [json]);
  if (!parsed || parsed.length === 0) return null;
  return (
    <div style={{ display: "flex", gap: "4px", flexWrap: "wrap" }}>
      {parsed.map((svc) => (
        <Badge key={svc} appearance="outline">
          {svc}
        </Badge>
      ))}
    </div>
  );
}

type SummaryBadge = {
  label: string;
  appearance: "outline" | "tint" | "filled";
  color?: "brand" | "danger" | "informative" | "success" | "warning";
};

function deriveImpactSummary(
  estimatedImpact: string | undefined,
  riskProfile: string | undefined,
): {
  primaryBenefit: string;
  effort: string;
  badges: SummaryBadge[];
} {
  const impact = tryParseJson(estimatedImpact);
  const risk = tryParseJson(riskProfile);
  const badges: SummaryBadge[] = [];

  const monthlySavings =
    typeof impact?.monthlySavings === "number"
      ? impact.monthlySavings
      : typeof impact?.costDelta === "number" && impact.costDelta < 0
        ? Math.abs(impact.costDelta)
        : undefined;
  const scoreImprovement =
    typeof impact?.scoreImprovement === "number"
      ? impact.scoreImprovement
      : typeof impact?.availabilityDelta === "number"
        ? impact.availabilityDelta
        : typeof impact?.securityDelta === "number"
          ? impact.securityDelta
          : undefined;
  const performanceDelta =
    typeof impact?.performanceDelta === "number"
      ? impact.performanceDelta
      : undefined;
  const timeToImplement =
    typeof impact?.timeToImplement === "string"
      ? impact.timeToImplement
      : undefined;
  const rollbackComplexity =
    typeof risk?.rollbackComplexity === "string"
      ? risk.rollbackComplexity
      : undefined;

  if (typeof monthlySavings === "number" && monthlySavings > 0) {
    badges.push({
      label: `Savings $${monthlySavings.toFixed(0)}/mo`,
      appearance: "filled",
      color: "success",
    });
  }

  if (typeof scoreImprovement === "number") {
    badges.push({
      label: `Posture +${scoreImprovement}`,
      appearance: "tint",
      color: "brand",
    });
  }

  if (typeof performanceDelta === "number" && performanceDelta !== 0) {
    badges.push({
      label: `Performance ${performanceDelta > 0 ? "+" : ""}${performanceDelta}`,
      appearance: "outline",
      color: performanceDelta > 0 ? "success" : "warning",
    });
  }

  if (rollbackComplexity) {
    badges.push({
      label: `Rollback ${rollbackComplexity}`,
      appearance: "outline",
      color: "informative",
    });
  }

  const primaryBenefit =
    typeof monthlySavings === "number" && monthlySavings > 0
      ? `$${monthlySavings.toFixed(0)}/month savings`
      : typeof scoreImprovement === "number"
        ? `Improve posture by ${scoreImprovement} points`
        : typeof performanceDelta === "number" && performanceDelta !== 0
          ? `${performanceDelta > 0 ? "Improve" : "Reduce"} performance variance by ${Math.abs(performanceDelta)} points`
          : "Reduce risk and improve operational posture";

  const effort = deriveEffortLabel(timeToImplement, rollbackComplexity);

  return { primaryBenefit, effort, badges };
}

function deriveEffortLabel(
  timeToImplement: string | undefined,
  rollbackComplexity: string | undefined,
): string {
  const time = (timeToImplement ?? "").toLowerCase();
  const rollback = (rollbackComplexity ?? "").toLowerCase();

  if (
    time.includes("day") ||
    time.includes("week") ||
    rollback.includes("high") ||
    rollback.includes("complex")
  ) {
    return "High";
  }

  if (
    time.includes("hour") ||
    time.includes("half") ||
    rollback.includes("medium") ||
    rollback.includes("moderate")
  ) {
    return "Medium";
  }

  return "Low";
}

function formatBlastRadiusSummary(
  blastRadius: BlastRadiusResponse | null,
): string {
  if (!blastRadius) return "Assessing blast radius";
  if (blastRadius.resourceCount === 0 && blastRadius.identityCount === 0) {
    return "Low — no dependent resources detected";
  }

  return `${blastRadius.resourceCount} resources • ${blastRadius.identityCount} identities`;
}

function formatImpactedServicesCount(json: string | undefined): string {
  if (!json) return "0 services listed";
  try {
    const parsed = JSON.parse(json) as string[];
    return `${parsed.length} service${parsed.length === 1 ? "" : "s"}`;
  } catch {
    return "1+ services";
  }
}

function deriveReadinessSummary({
  recommendationStatus,
  workflowStatus,
  changeSet,
  pullRequest,
}: {
  recommendationStatus?: string;
  workflowStatus: RecommendationWorkflowStatus | null;
  changeSet: ChangeSetDetail | null;
  pullRequest: PullRequestResult | null;
}): {
  label: string;
  description: string;
  badges: SummaryBadge[];
} {
  const normalizedStatus = normalizeRecommendationStatus(recommendationStatus);
  const badges: SummaryBadge[] = [];

  if (pullRequest?.previewMode) {
    badges.push({
      label: "Preview mode",
      appearance: "filled",
      color: "warning",
    });
  }

  if (changeSet?.content && changeSet.status !== "published") {
    badges.push({
      label: "IaC generated, not applied",
      appearance: "outline",
      color: "informative",
    });
  }

  if (workflowStatus?.stages.simulatePolicy) {
    badges.push({
      label: "Simulation available",
      appearance: "tint",
      color: "brand",
    });
  }

  if (changeSet?.status === "published" || pullRequest?.prUrl) {
    badges.push({
      label: "Provider-backed action",
      appearance: "filled",
      color: "success",
    });
    return {
      label: "Provider-backed action",
      description:
        "Approval enables a validated remediation path with published change artifacts and provider-backed delivery integration.",
      badges,
    };
  }

  if (changeSet?.content) {
    return {
      label: "IaC ready",
      description:
        "Approval will move this recommendation into validation, guardrail linting, and optional GitOps publishing using the generated IaC artifact.",
      badges,
    };
  }

  if (CHANGE_SET_ELIGIBLE_STATUSES.has(normalizedStatus)) {
    return {
      label: "Approval ready",
      description:
        "Approval unlocks policy simulation and change-set generation so operators can review impact, preview IaC, and publish safely.",
      badges,
    };
  }

  badges.push({
    label: "Preview only",
    appearance: "outline",
    color: "warning",
  });
  return {
    label: "Preview only",
    description:
      "This recommendation still needs workflow progression before change simulation or provider-backed execution can occur.",
    badges,
  };
}

/**
 * Sanitize text content by removing HTML tags and decoding HTML entities.
 * Prevents HTML injection and cleans up AI-generated content.
 */
function sanitizeText(text: string | undefined): string {
  if (!text) return "";

  // Remove HTML tags while preserving text content
  const sanitized = DOMPurify.sanitize(text, {
    ALLOWED_TAGS: [], // Strip all HTML tags
    ALLOWED_ATTR: [], // Strip all attributes
    KEEP_CONTENT: true, // Keep text content
  });

  // Decode common HTML entities (e.g., &quot; → ", &amp; → &)
  const decoded = sanitized
    .replace(/&quot;/g, '"')
    .replace(/&apos;/g, "'")
    .replace(/&amp;/g, "&")
    .replace(/&lt;/g, "<")
    .replace(/&gt;/g, ">")
    .replace(/&#x27;/g, "'");

  return decoded.trim();
}

/**
 * Clean and format evidence references into readable URLs or text.
 * Handles JSON-encoded arrays and HTML-encoded URLs.
 */
function formatEvidenceReferencesClean(
  evidence: Recommendation["evidenceReferences"],
): { text: string; urls: string[] } {
  const urls: string[] = [];

  if (!evidence) {
    return { text: "No evidence references recorded.", urls: [] };
  }

  let referencesArray: string[] = [];

  if (Array.isArray(evidence)) {
    referencesArray = evidence;
  } else if (typeof evidence === "string") {
    if (evidence.trim().length === 0) {
      return { text: "No evidence references recorded.", urls: [] };
    }
    try {
      const parsed = JSON.parse(evidence) as unknown;
      if (Array.isArray(parsed) && parsed.length > 0) {
        referencesArray = parsed as string[];
      } else if (
        parsed &&
        typeof parsed === "object" &&
        "references" in parsed &&
        Array.isArray((parsed as { references?: unknown }).references)
      ) {
        referencesArray = (parsed as { references: string[] }).references ?? [];
      } else {
        referencesArray = [evidence];
      }
    } catch {
      referencesArray = [evidence];
    }
  }

  // Clean URLs and extract valid ones
  const cleanReferences = referencesArray
    .map((ref) => {
      // Decode HTML entities
      const decoded = ref
        .replace(/&quot;/g, '"')
        .replace(/&http/g, "http") // Handle &http... encoding
        .replace(/&apos;/g, "'")
        .replace(/&amp;/g, "&")
        .trim();

      // If it looks like a URL, extract and save it
      if (decoded.startsWith("http://") || decoded.startsWith("https://")) {
        urls.push(decoded);
      }
      return decoded;
    })
    .filter((ref) => ref.length > 0);

  const text =
    cleanReferences.length > 0
      ? cleanReferences.join(" • ")
      : "No evidence references recorded.";

  return { text, urls };
}

function formatEvidenceReferences(
  evidence: Recommendation["evidenceReferences"],
): string {
  return formatEvidenceReferencesClean(evidence).text;
}

function ConfidenceExplainerCard({
  data,
  loading,
  error,
  onRefresh,
}: {
  data: ConfidenceExplainer | null;
  loading: boolean;
  error?: string;
  onRefresh: () => void;
}) {
  return (
    <Card>
      <Text size={600} weight="semibold">
        Confidence Explainer
      </Text>
      {loading && <Spinner label="Loading explainability…" />}
      {!loading && data && (
        <>
          <Text size={300}>
            {Math.round(data.confidenceScore * 100)}% confidence •{" "}
            {Math.round(data.trustScore * 100)}% trust (
            {data.trustLevel.toUpperCase()})
          </Text>
          <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
            {data.summary}
          </Text>
          {data.factors.map((factor) => (
            <Text key={factor} size={200}>
              • {factor}
            </Text>
          ))}
        </>
      )}
      <Button appearance="secondary" size="small" onClick={onRefresh}>
        Refresh Explainer
      </Button>
      {error && (
        <Text size={300} style={{ color: tokens.colorPaletteRedForeground1 }}>
          {error}
        </Text>
      )}
    </Card>
  );
}

function PolicyImpactSimulatorCard({
  result,
  busy,
  error,
  onSimulate,
  canSimulate,
  statusGuidance,
}: {
  result: PolicyImpactSimulationResult | null;
  busy: boolean;
  error?: string;
  onSimulate: () => void;
  canSimulate: boolean;
  statusGuidance: string;
}) {
  return (
    <Card>
      <div
        style={{
          display: "flex",
          gap: tokens.spacingHorizontalS,
          flexWrap: "wrap",
          alignItems: "center",
        }}
      >
        <Text size={600} weight="semibold">
          Policy Impact Simulator
        </Text>
        <Badge appearance="outline" color="brand">
          Simulation only
        </Badge>
      </div>
      <Button
        appearance="primary"
        size="small"
        onClick={onSimulate}
        disabled={busy || !canSimulate}
      >
        {busy ? "Simulating…" : "Run Simulation"}
      </Button>
      {!canSimulate && (
        <Text size={200} style={{ color: tokens.colorNeutralForeground3 }}>
          {statusGuidance}
        </Text>
      )}
      {result && (
        <>
          <Text size={300}>
            Decision:{" "}
            <strong>
              {result.policyDecision === "safe_to_approve"
                ? "Safe To Approve"
                : "Review Required"}
            </strong>{" "}
            • Threshold {result.policyThreshold}
          </Text>
          <Text size={200}>
            Confidence: {Math.round(result.simulation.confidence * 100)}%
          </Text>
          {result.reasons.map((reason) => (
            <Text key={reason} size={200}>
              • {reason}
            </Text>
          ))}
        </>
      )}
      {error && (
        <Text size={300} style={{ color: tokens.colorPaletteRedForeground1 }}>
          {error}
        </Text>
      )}
    </Card>
  );
}

function TrustLensCard({
  data,
  styles,
}: {
  data: Recommendation;
  styles: ReturnType<typeof useStyles>;
}) {
  const trustScore = Number(data.trustScore ?? data.confidenceScore ?? 0);
  const trustLevel = String(data.trustLevel ?? "medium");
  const evidence = Number(data.evidenceCompleteness ?? 0);
  const freshnessDays = Number(data.freshnessDays ?? 0);
  const riskScore = Number(data.riskScore ?? 0);

  const trustBadgeColor =
    trustLevel === "high"
      ? ("success" as const)
      : trustLevel === "low"
        ? ("danger" as const)
        : ("warning" as const);

  return (
    <Card>
      <Text size={600} weight="semibold">
        Trust Lens
      </Text>
      <div className={styles.badgeRow}>
        <Badge appearance="filled" color={trustBadgeColor}>
          {trustLevel.toUpperCase()}
        </Badge>
        <Badge appearance="tint" color="brand">
          Trust {(trustScore * 100).toFixed(0)}%
        </Badge>
        <Badge appearance="outline">
          Evidence {(evidence * 100).toFixed(0)}%
        </Badge>
        <Badge appearance="outline">Freshness {freshnessDays}d</Badge>
        <Badge appearance="outline">Risk {(riskScore * 100).toFixed(0)}%</Badge>
      </div>
      <Text size={200} className={styles.subtle}>
        Trust combines confidence, evidence completeness, and recency. Risk uses
        priority and blast-radius signals to help order the queue.
      </Text>
    </Card>
  );
}

function parseValidationResult(json?: string | null): {
  passed: boolean;
  errors: string[];
  warnings: string[];
  validatedAt?: string;
} | null {
  if (!json) return null;
  try {
    const parsed = JSON.parse(json) as {
      passed?: boolean;
      errors?: string[];
      warnings?: string[];
      validatedAt?: string;
    };
    return {
      passed: Boolean(parsed.passed),
      errors: parsed.errors ?? [],
      warnings: parsed.warnings ?? [],
      validatedAt: parsed.validatedAt,
    };
  } catch {
    return null;
  }
}

function ChangeSetValidationCard({
  changeSet,
  workflowStatus,
  recommendationId,
  avmExamples,
  avmExamplesLoading,
  avmExamplesError,
  onGenerateAvmExamples,
  recommendationStatus,
  canCreateChangeSet,
  statusGuidance,
  loading,
  error,
  onCreateChangeSet,
  onValidate,
  onPublish,
  onCreatePullRequest,
  createBusy,
  busy,
  publishBusy,
  publishError,
  prBusy,
  prError,
  pullRequest,
  repoUrl,
  onRepoUrlChange,
  targetBranch,
  onTargetBranchChange,
  componentName,
  onComponentNameChange,
  componentVersion,
  onComponentVersionChange,
  guardrail,
  guardrailBusy,
  guardrailError,
  onGuardrailLint,
}: {
  changeSet: ChangeSetDetail | null;
  workflowStatus: RecommendationWorkflowStatus | null;
  recommendationId: string;
  avmExamples: RecommendationIacExamples | null;
  avmExamplesLoading: boolean;
  avmExamplesError?: string;
  onGenerateAvmExamples: () => void;
  recommendationStatus?: string;
  canCreateChangeSet: boolean;
  statusGuidance: string;
  loading: boolean;
  error?: string;
  onCreateChangeSet: () => void;
  onValidate: () => void;
  onPublish: () => void;
  onCreatePullRequest: () => void;
  createBusy: boolean;
  busy: boolean;
  publishBusy: boolean;
  publishError?: string;
  prBusy: boolean;
  prError?: string;
  pullRequest: PullRequestResult | null;
  repoUrl: string;
  onRepoUrlChange: (value: string) => void;
  targetBranch: string;
  onTargetBranchChange: (value: string) => void;
  componentName: string;
  onComponentNameChange: (value: string) => void;
  componentVersion: string;
  onComponentVersionChange: (value: string) => void;
  guardrail: GuardrailLintResult | null;
  guardrailBusy: boolean;
  guardrailError?: string;
  onGuardrailLint: () => void;
}) {
  const styles = useStyles();
  const validation = useMemo(
    () => parseValidationResult(changeSet?.validationResult),
    [changeSet?.validationResult],
  );

  const policyReady = validation?.passed ?? false;
  const guardrailReady = guardrail?.passed ?? false;
  const publishReady = changeSet?.status === "validated";
  const prReady = changeSet?.status === "published";
  const prConfigValid = repoUrl.trim().length > 0;
  const normalizedRecommendationStatus =
    normalizeRecommendationStatus(recommendationStatus);

  void workflowStatus;
  void recommendationStatus;
  void canCreateChangeSet;
  void statusGuidance;
  void loading;
  void error;
  void onCreateChangeSet;
  void onValidate;
  void onPublish;
  void onCreatePullRequest;
  void createBusy;
  void busy;
  void publishBusy;
  void publishError;
  void prBusy;
  void prError;
  void pullRequest;
  void repoUrl;
  void onRepoUrlChange;
  void targetBranch;
  void onTargetBranchChange;
  void componentName;
  void onComponentNameChange;
  void componentVersion;
  void onComponentVersionChange;
  void guardrail;
  void guardrailBusy;
  void guardrailError;
  void onGuardrailLint;

  const persistedStages = workflowStatus?.stages;

  void persistedStages;
  void policyReady;
  void guardrailReady;
  void publishReady;
  void prReady;
  void prConfigValid;
  void normalizedRecommendationStatus;

  return (
    <Card>
      <div className={styles.badgeCluster}>
        <Text size={600} weight="semibold">
          Foundry Remediation Agent
        </Text>
        <Badge appearance="outline" color="brand">
          Foundry-generated IaC guidance
        </Badge>
        <Badge appearance="outline" color="warning">
          Review before apply
        </Badge>
      </div>
      <Text size={200} className={styles.subtle}>
        Recommendation {recommendationId} now uses a Foundry-hosted agent
        grounded in recommendation context plus published AVM module versions.
      </Text>

      <div className={styles.actionRow}>
        <Button
          appearance="primary"
          onClick={onGenerateAvmExamples}
          disabled={avmExamplesLoading}
        >
          {avmExamplesLoading
            ? "Generating…"
            : "Generate agent remediation IaC"}
        </Button>
      </div>

      {avmExamplesError && (
        <Text size={300} style={{ color: tokens.colorPaletteRedForeground1 }}>
          {avmExamplesError}
        </Text>
      )}

      {avmExamples && (
        <>
          <Text size={300}>
            Generated by: <strong>{avmExamples.generatedBy}</strong>
          </Text>
          <Text size={300}>
            {avmExamples.summary}
          </Text>
          <Text size={200} className={styles.subtle}>
            Generated at {new Date(avmExamples.generatedAtUtc).toLocaleString()}
          </Text>

          {avmExamples.citedModules?.length > 0 && (
            <>
              <Text
                size={300}
                weight="semibold"
                style={{ marginTop: tokens.spacingVerticalS }}
              >
                Grounding modules
              </Text>
              {avmExamples.citedModules.map((module, index) => (
                <Text key={`${module.bicepModulePath}-${index}`} size={200}>
                  • Bicep {module.bicepModulePath}@{module.bicepVersion} • Terraform {module.terraformModuleName}@{module.terraformVersion}
                </Text>
              ))}
            </>
          )}

          <Text size={300} weight="semibold" style={{ marginTop: tokens.spacingVerticalS }}>
            Bicep
          </Text>
          <pre className={styles.iacCode}>{avmExamples.bicepExample}</pre>

          <Text size={300} weight="semibold" style={{ marginTop: tokens.spacingVerticalS }}>
            Terraform
          </Text>
          <pre className={styles.iacCode}>{avmExamples.terraformExample}</pre>

          <Text size={200} className={styles.subtle}>
            Sources: {avmExamples.evidenceUrls.join(" • ")}
          </Text>
        </>
      )}

      {changeSet && (
        <Text size={200} className={styles.subtle}>
          Existing change set {changeSet.id} ({changeSet.status}) remains
          accessible for historical traceability.
        </Text>
      )}
    </Card>
  );
}

function ValueRealizationCard({
  changeSet,
  result,
  loading,
  error,
}: {
  changeSet: ChangeSetDetail | null;
  result: ValueRealizationResult | null;
  loading: boolean;
  error?: string;
}) {
  return (
    <Card>
      <Text size={600} weight="semibold">
        Value Realization
      </Text>
      {!changeSet && (
        <Text size={300}>
          Value realization starts after a change set is created.
        </Text>
      )}
      {loading && <Spinner label="Loading realization…" />}
      {!loading && changeSet && result && (
        <>
          <Text size={300}>Status: {result.status}</Text>
          {result.deltas && (
            <div
              style={{ display: "flex", flexDirection: "column", gap: "4px" }}
            >
              {Object.entries(result.deltas).map(([category, delta]) => (
                <Text key={category} size={200}>
                  {category}: {delta.scoreDelta > 0 ? "+" : ""}
                  {delta.scoreDelta.toFixed(1)} pts
                </Text>
              ))}
            </div>
          )}
        </>
      )}
      {error && (
        <Text size={300} style={{ color: tokens.colorPaletteRedForeground1 }}>
          {error}
        </Text>
      )}
    </Card>
  );
}

function AgentReplayPanel({
  analysisRunId,
  accessToken,
}: {
  analysisRunId?: string;
  accessToken?: string;
}) {
  const [loading, setLoading] = useState(false);
  const [messages, setMessages] = useState<
    Array<{
      id: string;
      agentName: string;
      messageType: string;
      payload?: string;
      createdAt: string;
    }>
  >([]);
  const [error, setError] = useState<string | undefined>();

  useEffect(() => {
    if (!analysisRunId) return;
    let cancelled = false;
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setLoading(true);

    setError(undefined);

    controlPlaneApi
      .getAgentMessages(analysisRunId, accessToken)
      .then((result) => {
        if (cancelled) return;
        setMessages(result.value ?? []);
      })
      .catch((e: unknown) => {
        if (cancelled) return;
        setError(e instanceof Error ? e.message : String(e));
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });

    return () => {
      cancelled = true;
    };
  }, [analysisRunId, accessToken]);

  return (
    <Card>
      <Text size={600} weight="semibold">
        Agent Replay
      </Text>
      {!analysisRunId && (
        <Text size={300}>No analysis run linked to this recommendation.</Text>
      )}
      {loading && <Spinner label="Loading agent messages…" />}
      {error && (
        <Text size={300} style={{ color: tokens.colorPaletteRedForeground1 }}>
          {error}
        </Text>
      )}
      {!loading && !error && messages.length > 0 && (
        <div style={{ display: "flex", flexDirection: "column", gap: "6px" }}>
          {messages.slice(0, 15).map((msg) => (
            <Card key={msg.id}>
              <Text weight="semibold" size={300}>
                {msg.agentName} • {msg.messageType}
              </Text>
              <Text
                size={200}
                style={{ color: tokens.colorNeutralForeground3 }}
              >
                {new Date(msg.createdAt).toLocaleString()}
              </Text>
              {msg.payload && (
                <Text size={200} style={{ whiteSpace: "pre-wrap" }}>
                  {msg.payload.length > 300
                    ? `${msg.payload.slice(0, 300)}…`
                    : msg.payload}
                </Text>
              )}
            </Card>
          ))}
        </div>
      )}
      {!loading && !error && analysisRunId && messages.length === 0 && (
        <Text size={300}>No agent messages recorded yet.</Text>
      )}
    </Card>
  );
}
