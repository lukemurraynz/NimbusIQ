import { useEffect, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import {
  Badge,
  Button,
  Card,
  Spinner,
  Text,
  makeStyles,
  tokens,
} from "@fluentui/react-components";
import {
  controlPlaneApi,
  type Recommendation,
} from "../services/controlPlaneApi";
import { useAccessToken } from "../auth/useAccessToken";
import { RECOMMENDATION_WORKFLOW_STATUS } from "../constants/recommendationWorkflowStatus";

const useStyles = makeStyles({
  container: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalL,
    padding: tokens.spacingHorizontalXL,
  },
  conflictList: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalM,
  },
  conflictRow: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalS,
  },
  rowHeader: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "center",
    gap: tokens.spacingHorizontalM,
    flexWrap: "wrap",
  },
  recPair: {
    display: "grid",
    gridTemplateColumns: "1fr 1fr",
    gap: tokens.spacingHorizontalM,
    "@media (max-width: 960px)": {
      gridTemplateColumns: "1fr",
    },
  },
  recCard: {
    padding: tokens.spacingHorizontalM,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  decisionGrid: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
    gap: tokens.spacingHorizontalM,
  },
  matrixCard: {
    padding: tokens.spacingHorizontalM,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
  },
});

type Conflict = {
  key: string;
  resourceId: string;
  first: Recommendation;
  second: Recommendation;
};

function resourceName(resourceId: string): string {
  const parts = resourceId.split("/");
  return parts[parts.length - 1] || resourceId;
}

function describeConflict(first: Recommendation, second: Recommendation) {
  const firstGoal = first.category ?? "governance";
  const secondGoal = second.category ?? "operations";
  const recommended =
    Number(first.riskWeightedScore ?? 0) >= Number(second.riskWeightedScore ?? 0)
      ? first
      : second;
  const deferred = recommended.id === first.id ? second : first;

  return {
    why: `${firstGoal} optimizes for ${first.actionType}, while ${secondGoal} optimizes for ${second.actionType} on the same resource.`,
    recommended,
    deferred,
    rationale: `NimbusIQ recommends prioritizing ${recommended.category ?? "the higher-priority option"} because it currently carries the stronger queue pressure and lower tolerance for deferral.`,
  };
}

export function GovernanceConflictsPage() {
  const styles = useStyles();
  const navigate = useNavigate();
  const { accessToken } = useAccessToken();
  const [items, setItems] = useState<Recommendation[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | undefined>();

  useEffect(() => {
    document.title = "NimbusIQ — Governance Conflicts";
  }, []);

  useEffect(() => {
    let cancelled = false;
    setLoading(true);
    setError(undefined);

    controlPlaneApi
      .listRecommendations(
        {
          status: `${RECOMMENDATION_WORKFLOW_STATUS.pending},${RECOMMENDATION_WORKFLOW_STATUS.pendingApproval},${RECOMMENDATION_WORKFLOW_STATUS.manualReview}`,
          orderBy: "riskweighted",
          limit: 200,
        },
        accessToken,
      )
      .then((response) => {
        if (cancelled) {
          return;
        }

        setItems(response.value ?? []);
      })
      .catch((e: unknown) => {
        if (cancelled) {
          return;
        }

        setError(e instanceof Error ? e.message : String(e));
      })
      .finally(() => {
        if (!cancelled) {
          setLoading(false);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [accessToken]);

  const conflicts = useMemo<Conflict[]>(() => {
    const byResource = new Map<string, Recommendation[]>();
    for (const rec of items) {
      if (!rec.resourceId) {
        continue;
      }

      const list = byResource.get(rec.resourceId) ?? [];
      list.push(rec);
      byResource.set(rec.resourceId, list);
    }

    const pairs: Conflict[] = [];

    for (const [resourceId, recs] of byResource.entries()) {
      if (recs.length < 2) {
        continue;
      }

      const sorted = [...recs].sort(
        (a, b) =>
          Number(b.riskWeightedScore ?? 0) - Number(a.riskWeightedScore ?? 0),
      );

      for (let i = 0; i < sorted.length; i++) {
        for (let j = i + 1; j < sorted.length; j++) {
          const first = sorted[i];
          const second = sorted[j];
          const sameActionType =
            (first.actionType ?? "") === (second.actionType ?? "");
          const sameCategory =
            (first.category ?? "") === (second.category ?? "");

          if (sameActionType && sameCategory) {
            continue;
          }

          pairs.push({
            key: `${resourceId}:${first.id}:${second.id}`,
            resourceId,
            first,
            second,
          });

          if (pairs.length >= 20) {
            return pairs;
          }
        }
      }
    }

    return pairs;
  }, [items]);

  return (
    <div className={styles.container}>
      <Text size={900} weight="bold">
        Governance Conflicts
      </Text>
      <Text size={300}>
        Detects recommendations that may conflict on the same resource and lets
        you open negotiation preloaded with both options.
      </Text>

      {loading && <Spinner label="Finding conflicts..." />}

      {!loading && error && (
        <Card>
          <Text weight="semibold">Failed to load governance conflicts</Text>
          <Text>{error}</Text>
        </Card>
      )}

      {!loading && !error && conflicts.length === 0 && (
        <Card>
          <Text weight="semibold">No active conflicts found</Text>
          <Text>
            Pending recommendations currently do not present obvious
            cross-category conflicts on the same resource.
          </Text>
        </Card>
      )}

      {!loading && !error && conflicts.length > 0 && (
        <div className={styles.conflictList}>
          {conflicts.map((conflict) => (
            <Card key={conflict.key} className={styles.conflictRow}>
              {(() => {
                const decision = describeConflict(
                  conflict.first,
                  conflict.second,
                );
                return (
                  <>
              <div className={styles.rowHeader}>
                <div>
                  <Text weight="semibold">
                    {resourceName(conflict.resourceId)}
                  </Text>
                  <Text size={200}>{conflict.resourceId}</Text>
                </div>
                <Button
                  appearance="primary"
                  onClick={() =>
                    navigate(
                      `/governance?first=${encodeURIComponent(conflict.first.id)}&second=${encodeURIComponent(conflict.second.id)}`,
                    )
                  }
                >
                  Open Negotiation
                </Button>
              </div>
              <Text size={200}>{decision.why}</Text>
              <div className={styles.recPair}>
                {[conflict.first, conflict.second].map((rec) => (
                  <div key={rec.id} className={styles.recCard}>
                    <Text weight="semibold">
                      {rec.title ?? rec.recommendationType}
                    </Text>
                    <Text size={200}>{rec.description ?? rec.resourceId}</Text>
                    <div
                      style={{
                        display: "flex",
                        gap: tokens.spacingHorizontalS,
                        marginTop: tokens.spacingVerticalS,
                      }}
                    >
                      <Badge appearance="outline">
                        {rec.category ?? "General"}
                      </Badge>
                      <Badge appearance="outline">{rec.actionType}</Badge>
                      <Badge appearance="tint" color="warning">
                        {Math.round(Number(rec.riskWeightedScore ?? 0) * 100)}%
                      </Badge>
                      <Badge appearance="outline">
                        Approvals {rec.receivedApprovals}/{rec.requiredApprovals}
                      </Badge>
                    </div>
                  </div>
                ))}
              </div>
              <div className={styles.decisionGrid}>
                <div className={styles.matrixCard}>
                  <Text weight="semibold">Recommended option</Text>
                  <Text>{decision.recommended.title ?? decision.recommended.recommendationType}</Text>
                  <Text size={200}>
                    Better aligns with immediate {decision.recommended.category ?? "platform"} risk and queue urgency.
                  </Text>
                </div>
                <div className={styles.matrixCard}>
                  <Text weight="semibold">Trade-off accepted</Text>
                  <Text size={200}>
                    Defer {decision.deferred.category ?? "secondary"} optimization temporarily to avoid competing changes on the same resource.
                  </Text>
                </div>
                <div className={styles.matrixCard}>
                  <Text weight="semibold">Required approvals</Text>
                  <Text size={200}>
                    {decision.recommended.receivedApprovals}/{decision.recommended.requiredApprovals} approvals received for the recommended path.
                  </Text>
                </div>
                <div className={styles.matrixCard}>
                  <Text weight="semibold">Why this is safer</Text>
                  <Text size={200}>{decision.rationale}</Text>
                </div>
              </div>
                  </>
                );
              })()}
            </Card>
          ))}
        </div>
      )}
    </div>
  );
}
