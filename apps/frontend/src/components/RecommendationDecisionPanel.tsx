import { useEffect, useRef, useState, useCallback } from "react";
import {
  Badge,
  Button,
  Card,
  Checkbox,
  Text,
  Textarea,
  makeStyles,
  tokens,
} from "@fluentui/react-components";
import {
  ApiRequestError,
  controlPlaneApi,
  type RecommendationGroundingProvenance,
} from "../services/controlPlaneApi";
import { useAccessToken } from "../auth/useAccessToken";

const useStyles = makeStyles({
  card: {
    width: "100%",
    boxSizing: "border-box",
  },
  root: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalM,
    width: "100%",
  },
  actions: {
    display: "flex",
    gap: tokens.spacingHorizontalM,
    flexWrap: "wrap",
  },
  subtle: {
    color: tokens.colorNeutralForeground3,
  },
  shortcutHint: {
    color: tokens.colorNeutralForeground4,
    fontSize: tokens.fontSizeBase100,
  },
  textarea: {
    width: "100%",
    minHeight: "84px",
    boxSizing: "border-box",
  },
  contextGrid: {
    display: "grid",
    gridTemplateColumns: "1fr 1fr",
    gap: tokens.spacingHorizontalS,
  },
  contextItem: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalXXS,
  },
});

function normalizeGroundingSourceLabel(source: string | undefined): string {
  if (!source) return "Seeded analysis";
  if (source.toLowerCase() === "learn_mcp") return "Learn MCP";
  if (source.toLowerCase() === "seeded_rule") return "Seeded rule";

  return source
    .split("_")
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join(" ");
}

function formatTimestamp(value: string | undefined): string {
  if (!value) return "Not recorded";
  const parsed = new Date(value);
  return Number.isNaN(parsed.getTime()) ? value : parsed.toLocaleString();
}

function formatPercent(value: number | undefined): string {
  if (typeof value !== "number" || Number.isNaN(value)) return "Not scored";
  return `${Math.round(value * 100)}%`;
}

export function RecommendationDecisionPanel(props: {
  recommendationId: string;
  status: string;
  priority?: string;
  actionType?: string;
  resourceId?: string;
  onChanged?: () => void;
}) {
  const styles = useStyles();
  const { accessToken } = useAccessToken();
  const [comments, setComments] = useState("");
  const [reason, setReason] = useState("");
  const [busy, setBusy] = useState(false);
  const [workflowBusy, setWorkflowBusy] = useState(false);
  const [error, setError] = useState<string | undefined>(undefined);
  const [previewBusy, setPreviewBusy] = useState(false);
  const [impactPreview, setImpactPreview] = useState<{
    policyDecision: string;
    projectedScores?: Record<string, number>;
    reasons?: string[];
  } | null>(null);
  const [grounding, setGrounding] =
    useState<RecommendationGroundingProvenance | null>(null);
  const [groundingBusy, setGroundingBusy] = useState(false);
  const [groundingError, setGroundingError] = useState<string | undefined>(
    undefined,
  );
  const [guardrails, setGuardrails] = useState({
    riskReviewed: false,
    impactReviewed: false,
    rollbackReviewed: false,
    evidenceReviewed: false,
  });
  const panelRef = useRef<HTMLDivElement>(null);

  const status = props.status.toLowerCase();
  const canDecide =
    status === "pending" || status === "pending_approval" || status === "draft";
  const isTerminal = status === "approved" || status === "rejected";
  const canWorkflow = !isTerminal;
  const approvalsReady = Object.values(guardrails).every(Boolean);

  useEffect(() => {
    let cancelled = false;

    async function loadGrounding() {
      setGroundingBusy(true);
      setGroundingError(undefined);
      try {
        const lineage = await controlPlaneApi.getRecommendationLineage(
          props.recommendationId,
          accessToken,
        );
        if (cancelled) return;
        setGrounding(lineage.provenance ?? null);
      } catch (e) {
        if (cancelled) return;
        setGroundingError(e instanceof Error ? e.message : String(e));
      } finally {
        if (!cancelled) {
          setGroundingBusy(false);
        }
      }
    }

    void loadGrounding();

    return () => {
      cancelled = true;
    };
  }, [props.recommendationId, accessToken]);

  const buildApprovalIntentHash = useCallback(async (): Promise<string> => {
    const normalizedStatus = props.status.trim().toLowerCase();
    const canonical = [
      props.recommendationId,
      normalizedStatus,
      comments.trim(),
    ].join("|");

    const encoder = new TextEncoder();
    const data = encoder.encode(canonical);
    const digest = await crypto.subtle.digest("SHA-256", data);
    return Array.from(new Uint8Array(digest))
      .map((b) => b.toString(16).padStart(2, "0"))
      .join("");
  }, [props.recommendationId, props.status, comments]);

  function mapDecisionError(
    err: unknown,
    action: "approve" | "reject",
  ): string {
    if (err instanceof ApiRequestError) {
      if (err.status === 404) {
        return `Cannot ${action}: decision endpoint not available on this API deployment (404).`;
      }

      if (err.status === 401 || err.status === 403) {
        return `Cannot ${action}: missing required approval permissions.`;
      }

      if (err.correlationId) {
        return `Cannot ${action}: ${err.message} (correlation: ${err.correlationId}).`;
      }

      return `Cannot ${action}: ${err.message}`;
    }

    return `Cannot ${action}: ${err instanceof Error ? err.message : String(err)}`;
  }

  const approve = useCallback(async () => {
    setBusy(true);
    setError(undefined);
    try {
      const intentHash = await buildApprovalIntentHash();
      await controlPlaneApi.approveRecommendation(
        props.recommendationId,
        comments,
        intentHash,
        accessToken,
      );
      props.onChanged?.();
    } catch (e) {
      setError(mapDecisionError(e, "approve"));
    } finally {
      setBusy(false);
    }
  }, [buildApprovalIntentHash, props, comments, accessToken]);

  async function reject() {
    setBusy(true);
    setError(undefined);
    try {
      await controlPlaneApi.rejectRecommendation(
        props.recommendationId,
        reason || "Rejected",
        accessToken,
      );
      props.onChanged?.();
    } catch (e) {
      setError(mapDecisionError(e, "reject"));
    } finally {
      setBusy(false);
    }
  }

  async function updateWorkflowStatus(
    nextStatus: "planned" | "in_progress" | "verified",
  ) {
    setWorkflowBusy(true);
    setError(undefined);
    try {
      await controlPlaneApi.updateRecommendationStatus(
        props.recommendationId,
        {
          status: nextStatus,
          comments:
            nextStatus === "planned"
              ? comments || "Added to execution plan."
              : nextStatus === "in_progress"
                ? comments || "Execution started."
                : comments || "Implementation verified.",
        },
        accessToken,
      );
      props.onChanged?.();
    } catch (e) {
      setError(
        `Cannot update workflow: ${e instanceof Error ? e.message : String(e)}`,
      );
    } finally {
      setWorkflowBusy(false);
    }
  }

  async function previewImpact() {
    setPreviewBusy(true);
    setError(undefined);
    try {
      const preview = await controlPlaneApi.simulateRecommendationPolicyImpact(
        props.recommendationId,
        { policyThreshold: 60 },
        accessToken,
      );
      setImpactPreview({
        policyDecision: preview.policyDecision,
        projectedScores: preview.simulation?.projectedScores,
        reasons: preview.reasons,
      });
    } catch (e) {
      setError(
        `Cannot preview impact: ${e instanceof Error ? e.message : String(e)}`,
      );
    } finally {
      setPreviewBusy(false);
    }
  }

  // Keyboard shortcut: Ctrl+Enter to approve when the panel (or its children) has focus
  useEffect(() => {
    if (!canDecide) return;

    function onKeyDown(e: KeyboardEvent) {
      if (e.key === "Enter" && (e.ctrlKey || e.metaKey) && !busy) {
        if (panelRef.current?.contains(document.activeElement)) {
          e.preventDefault();
          void approve();
        }
      }
    }

    document.addEventListener("keydown", onKeyDown);
    return () => document.removeEventListener("keydown", onKeyDown);
  }, [canDecide, busy, approve]);

  return (
    <Card className={styles.card}>
      <div className={styles.root} ref={panelRef}>
        <Text size={600} weight="semibold">
          Decision
        </Text>
        <Text className={styles.subtle} size={200}>
          Use this panel to simulate policy impact, confirm readiness checks,
          and then approve or reject with an auditable rationale.
        </Text>
        {!canDecide && (
          <Text className={styles.subtle} size={300}>
            This recommendation is in status{" "}
            <Text weight="semibold">{props.status}</Text> and cannot be changed.
          </Text>
        )}

        {canDecide && (
          <>
            <div className={styles.actions}>
              <Button
                appearance="secondary"
                disabled={previewBusy || busy}
                onClick={() => void previewImpact()}
              >
                {previewBusy ? "Previewing…" : "Preview Impact"}
              </Button>
              {impactPreview && (
                <Badge
                  appearance="tint"
                  color={
                    impactPreview.policyDecision === "safe_to_approve"
                      ? "success"
                      : "warning"
                  }
                >
                  {impactPreview.policyDecision === "safe_to_approve"
                    ? "Safe to approve"
                    : "Review required"}
                </Badge>
              )}
            </div>
            {impactPreview?.projectedScores && (
              <Text className={styles.subtle} size={200}>
                Projected scores:{" "}
                {Object.entries(impactPreview.projectedScores)
                  .map(([key, value]) => `${key} ${Math.round(value)}`)
                  .join(" • ")}
              </Text>
            )}
            {impactPreview?.reasons && impactPreview.reasons.length > 0 && (
              <Text className={styles.subtle} size={200}>
                {impactPreview.reasons.join(" • ")}
              </Text>
            )}
            <div>
              <div>
                <Text weight="semibold" block>
                  Learn MCP context
                </Text>
                <Text className={styles.subtle} size={200} block>
                  Grounding context explains what external guidance was used to
                  enrich this recommendation before approval.
                </Text>
                {groundingBusy && (
                  <Text className={styles.subtle} size={200}>
                    Loading grounding context...
                  </Text>
                )}
                {!groundingBusy && (
                  <div className={styles.contextGrid}>
                    <div className={styles.contextItem}>
                      <Text className={styles.subtle} size={200}>
                        Source
                      </Text>
                      <Text size={200}>
                        {normalizeGroundingSourceLabel(
                          grounding?.groundingSource,
                        )}
                      </Text>
                    </div>
                    <div className={styles.contextItem}>
                      <Text className={styles.subtle} size={200}>
                        Grounded at
                      </Text>
                      <Text size={200}>
                        {formatTimestamp(grounding?.groundingTimestampUtc)}
                      </Text>
                    </div>
                    <div className={styles.contextItem}>
                      <Text className={styles.subtle} size={200}>
                        Quality
                      </Text>
                      <Text size={200}>
                        {formatPercent(grounding?.groundingQuality)}
                      </Text>
                    </div>
                    <div className={styles.contextItem}>
                      <Text className={styles.subtle} size={200}>
                        Recency
                      </Text>
                      <Text size={200}>
                        {formatPercent(grounding?.groundingRecencyScore)}
                      </Text>
                    </div>
                  </div>
                )}
                <Text className={styles.subtle} size={200}>
                  Query: {grounding?.groundingQuery?.trim() || "Not recorded"}
                </Text>
                {grounding?.groundingToolRunId && (
                  <Text className={styles.subtle} size={200}>
                    Tool run: {grounding.groundingToolRunId}
                  </Text>
                )}
                {groundingError && (
                  <Text
                    size={200}
                    style={{ color: tokens.colorPaletteRedForeground1 }}
                  >
                    Unable to load Learn MCP context: {groundingError}
                  </Text>
                )}
              </div>

              <Text weight="semibold" block>
                Approval readiness checks
              </Text>
              <Text className={styles.subtle} size={200} block>
                Confirm these checks so approvals are explicit and auditable.
              </Text>
              <div
                style={{
                  display: "flex",
                  flexDirection: "column",
                  gap: tokens.spacingVerticalXS,
                }}
              >
                <Checkbox
                  label="Risk reviewed and acceptable"
                  checked={guardrails.riskReviewed}
                  onChange={(_, data) =>
                    setGuardrails((prev) => ({
                      ...prev,
                      riskReviewed: Boolean(data.checked),
                    }))
                  }
                />
                <Checkbox
                  label="Impact and trade-offs understood"
                  checked={guardrails.impactReviewed}
                  onChange={(_, data) =>
                    setGuardrails((prev) => ({
                      ...prev,
                      impactReviewed: Boolean(data.checked),
                    }))
                  }
                />
                <Checkbox
                  label="Rollback plan reviewed"
                  checked={guardrails.rollbackReviewed}
                  onChange={(_, data) =>
                    setGuardrails((prev) => ({
                      ...prev,
                      rollbackReviewed: Boolean(data.checked),
                    }))
                  }
                />
                <Checkbox
                  label="Evidence and confidence validated"
                  checked={guardrails.evidenceReviewed}
                  onChange={(_, data) =>
                    setGuardrails((prev) => ({
                      ...prev,
                      evidenceReviewed: Boolean(data.checked),
                    }))
                  }
                />
              </div>
            </div>
            <div>
              <Text weight="semibold" block>
                Approval comments
              </Text>
              <Textarea
                className={styles.textarea}
                value={comments}
                onChange={(_, data) => setComments(data.value)}
                placeholder="Why is this safe and valuable? Link evidence if available."
              />
            </div>
            <div>
              <Text weight="semibold" block>
                Rejection reason
              </Text>
              <Textarea
                className={styles.textarea}
                value={reason}
                onChange={(_, data) => setReason(data.value)}
                placeholder="If rejecting, record the rationale for audit replay."
              />
            </div>
            <div className={styles.actions}>
              <Button
                appearance="primary"
                disabled={busy || !approvalsReady}
                onClick={() => void approve()}
                aria-keyshortcuts="Ctrl+Enter"
              >
                Approve
              </Button>
              <Button
                appearance="secondary"
                disabled={busy}
                onClick={() => void reject()}
              >
                Reject
              </Button>
            </div>
            <Text className={styles.shortcutHint}>
              Tip: press <kbd>Ctrl+Enter</kbd> to approve when this panel is
              focused.
            </Text>
          </>
        )}

        {canWorkflow && (
          <div>
            <Text size={300} weight="semibold" block>
              Workflow
            </Text>
            <div className={styles.actions}>
              <Button
                appearance="subtle"
                disabled={workflowBusy}
                onClick={() => void updateWorkflowStatus("planned")}
              >
                Plan
              </Button>
              <Button
                appearance="subtle"
                disabled={workflowBusy}
                onClick={() => void updateWorkflowStatus("in_progress")}
              >
                Start
              </Button>
              <Button
                appearance="subtle"
                disabled={workflowBusy}
                onClick={() => void updateWorkflowStatus("verified")}
              >
                Verify
              </Button>
            </div>
            {workflowBusy && (
              <Text className={styles.subtle} size={200}>
                Updating workflow state...
              </Text>
            )}
          </div>
        )}

        {error && (
          <Text size={300} style={{ color: tokens.colorPaletteRedForeground1 }}>
            {error}
          </Text>
        )}
      </div>
    </Card>
  );
}
