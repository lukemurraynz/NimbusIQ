import { useEffect, useMemo, useState } from "react";
import { useNavigate, useParams } from "react-router-dom";
import {
  Badge,
  Button,
  Card,
  Spinner,
  Text,
  makeStyles,
  tokens,
} from "@fluentui/react-components";
import { AzurePageHeader } from "../components/AzurePageHeader";
import { useAccessToken } from "../auth/useAccessToken";
import {
  controlPlaneApi,
  type ServiceGroupHealthResponse,
} from "../services/controlPlaneApi";

const useStyles = makeStyles({
  content: {
    padding: tokens.spacingHorizontalXXL,
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalL,
  },
  metricsGrid: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
    gap: tokens.spacingHorizontalM,
  },
  metricTile: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalXXS,
    minWidth: 0,
  },
  metricLabel: {
    color: tokens.colorNeutralForeground3,
  },
  metricValue: {
    fontWeight: tokens.fontWeightSemibold,
  },
  metricMeta: {
    color: tokens.colorNeutralForeground3,
  },
  sectionGrid: {
    display: "grid",
    gridTemplateColumns: "1fr 1fr",
    gap: tokens.spacingHorizontalL,
    "@media (max-width: 1000px)": {
      gridTemplateColumns: "1fr",
    },
  },
  list: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalXS,
  },
  listItem: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "center",
    gap: tokens.spacingHorizontalS,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
  },
  subtle: {
    color: tokens.colorNeutralForeground3,
  },
});

function priorityColor(priority: string) {
  switch (priority.toLowerCase()) {
    case "critical":
      return "danger" as const;
    case "high":
      return "warning" as const;
    case "medium":
      return "brand" as const;
    default:
      return "success" as const;
  }
}

export function ServiceGroupHealthPage() {
  const styles = useStyles();
  const { id } = useParams();
  const navigate = useNavigate();
  const { accessToken } = useAccessToken();
  const [data, setData] = useState<ServiceGroupHealthResponse | null>(null);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | null>(null);

  useEffect(() => {
    document.title = "NimbusIQ — Service Group Health";
  }, []);

  useEffect(() => {
    if (!id) return;
    let cancelled = false;
    // Set loading state before async operation (intentional sync setState in data fetch pattern)
    // eslint-disable-next-line react-hooks/set-state-in-effect
    setLoading(true);

    setError(null);
    void controlPlaneApi
      .getServiceGroupHealth(id, accessToken)
      .then((res) => {
        if (!cancelled) setData(res);
      })
      .catch((e: unknown) => {
        if (!cancelled) setError(e instanceof Error ? e.message : String(e));
      })
      .finally(() => {
        if (!cancelled) setLoading(false);
      });
    return () => {
      cancelled = true;
    };
  }, [id, accessToken]);

  const scores = useMemo(() => {
    if (!data) return [];
    return Object.entries(data.latestScores).map(([category, score]) => ({
      category,
      score: Math.round(score.score),
      confidence: Math.round(score.confidence * 100),
    }));
  }, [data]);

  return (
    <div>
      <AzurePageHeader
        title={data?.serviceGroup.name ?? "Service Group Health"}
        subtitle="Application-level risk, savings, and reliability health"
        commands={
          <Button
            appearance="secondary"
            onClick={() => navigate("/service-groups")}
          >
            Back to Service Groups
          </Button>
        }
      />
      <div className={styles.content}>
        {loading && <Spinner label="Loading service group health..." />}
        {error && (
          <Card>
            <Text weight="semibold">Failed to load service group health</Text>
            <Text className={styles.subtle}>{error}</Text>
          </Card>
        )}
        {!loading && !error && data && (
          <>
            <Card>
              <Text size={500} weight="semibold">
                Business Impact
              </Text>
              <div className={styles.metricsGrid}>
                <div className={styles.metricTile}>
                  <Text block className={styles.metricLabel}>
                    Outage Risk
                  </Text>
                  <Text block className={styles.metricValue}>
                    {data.businessImpact.outageRisk}
                  </Text>
                </div>
                <div className={styles.metricTile}>
                  <Text block className={styles.metricLabel}>
                    Compliance Exposure
                  </Text>
                  <Text block className={styles.metricValue}>
                    {data.businessImpact.complianceExposure}
                  </Text>
                </div>
                <div className={styles.metricTile}>
                  <Text block className={styles.metricLabel}>
                    Monthly Cost Opportunity
                  </Text>
                  <Text block className={styles.metricValue}>
                    ${data.businessImpact.monthlyCostOpportunity.toFixed(0)}
                  </Text>
                </div>
                <div className={styles.metricTile}>
                  <Text block className={styles.metricLabel}>
                    Sustainability Opportunities
                  </Text>
                  <Text block className={styles.metricValue}>
                    {data.businessImpact.sustainabilityOpportunity}
                  </Text>
                </div>
              </div>
            </Card>

            <Card>
              <Text size={500} weight="semibold">
                Score Health
              </Text>
              <div className={styles.metricsGrid}>
                {scores.map((s) => (
                  <div key={s.category} className={styles.metricTile}>
                    <Text block className={styles.metricLabel}>
                      {s.category}
                    </Text>
                    <Text block className={styles.metricValue}>
                      {s.score}/100
                    </Text>
                    <Text block className={styles.metricMeta}>
                      Confidence {s.confidence}%
                    </Text>
                  </div>
                ))}
              </div>
            </Card>

            <div className={styles.sectionGrid}>
              <Card>
                <Text size={500} weight="semibold">
                  Priority Inbox
                </Text>
                <Text className={styles.subtle} size={200}>
                  Do now
                </Text>
                <div className={styles.list}>
                  {data.priorityInbox.doNow.map((r) => (
                    <div key={r.id} className={styles.listItem}>
                      <div>
                        <Text weight="semibold">{r.title}</Text>
                        <Text className={styles.subtle} size={200}>
                          Due {new Date(r.dueDate).toLocaleDateString()}
                        </Text>
                      </div>
                      <Badge
                        color={priorityColor(r.priority)}
                        appearance="filled"
                      >
                        {r.priority}
                      </Badge>
                    </div>
                  ))}
                </div>
                <Text className={styles.subtle} size={200}>
                  This week
                </Text>
                <div className={styles.list}>
                  {data.priorityInbox.thisWeek.map((r) => (
                    <div key={r.id} className={styles.listItem}>
                      <Text>{r.title}</Text>
                      <Badge appearance="outline">
                        {Math.round(r.queueScore * 100)}%
                      </Badge>
                    </div>
                  ))}
                </div>
              </Card>

              <Card>
                <Text size={500} weight="semibold">
                  Top Risks and Savings
                </Text>
                <Text className={styles.subtle} size={200}>
                  Risks
                </Text>
                <div className={styles.list}>
                  {data.topRisks.map((r) => (
                    <div key={r.id} className={styles.listItem}>
                      <Text>{r.title}</Text>
                      <Badge
                        color={priorityColor(r.priority)}
                        appearance="filled"
                      >
                        {r.priority}
                      </Badge>
                    </div>
                  ))}
                </div>
                <Text className={styles.subtle} size={200}>
                  Savings
                </Text>
                <div className={styles.list}>
                  {data.topSavings.map((s) => (
                    <div key={s.id} className={styles.listItem}>
                      <Text>{s.title}</Text>
                      <Badge appearance="tint" color="success">
                        ${Math.round(s.monthlySavings)}/mo
                      </Badge>
                    </div>
                  ))}
                </div>
                <Text className={styles.subtle} size={200}>
                  Reliability weak points
                </Text>
                <div className={styles.list}>
                  {data.reliabilityWeakPoints.map((w) => (
                    <div key={w.dimension} className={styles.listItem}>
                      <Text>{w.dimension}</Text>
                      <Badge appearance="outline">
                        {Math.round(w.score * 100)}%
                      </Badge>
                    </div>
                  ))}
                </div>
              </Card>
            </div>

            <Button
              appearance="primary"
              onClick={() =>
                navigate(
                  `/recommendations?serviceGroupId=${data.serviceGroup.id}`,
                )
              }
            >
              Open Service Group Recommendations
            </Button>
          </>
        )}
      </div>
    </div>
  );
}
