import { useEffect, useMemo, useState } from "react";
import {
  Badge,
  Card,
  CardHeader,
  Dropdown,
  Label,
  Option,
  Spinner,
  Text,
  makeStyles,
  tokens,
  DataGrid,
  DataGridHeader,
  DataGridHeaderCell,
  DataGridBody,
  DataGridRow,
  DataGridCell,
  TableCellLayout,
  createTableColumn,
  type TableColumnDefinition,
  useId,
} from "@fluentui/react-components";
import { AzurePageHeader } from "../components/AzurePageHeader";
import { useAccessToken } from "../auth/useAccessToken";
import { controlPlaneApi } from "../services/controlPlaneApi";
import type {
  CostEvidenceItem,
  RoiDashboardData,
  ServiceGroup,
  TopSaverItem,
} from "../services/controlPlaneApi";

const useStyles = makeStyles({
  container: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalXL,
    padding: tokens.spacingHorizontalXXL,
  },
  headerRow: {
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    gap: tokens.spacingHorizontalM,
    flexWrap: "wrap",
  },
  filterRow: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalM,
  },
  metricsGrid: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
    gap: tokens.spacingHorizontalL,
  },
  metricCard: {
    padding: tokens.spacingHorizontalL,
    minHeight: "120px",
  },
  metricValue: {
    fontSize: tokens.fontSizeHero700,
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: tokens.lineHeightHero700,
  },
  metricLabel: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase300,
  },
  sectionCard: {
    padding: tokens.spacingHorizontalXL,
  },
  insightText: {
    marginTop: tokens.spacingVerticalXS,
    color: tokens.colorNeutralForeground2,
  },
  insightsGrid: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(260px, 1fr))",
    gap: tokens.spacingHorizontalL,
    marginTop: tokens.spacingVerticalM,
  },
  insightCard: {
    padding: tokens.spacingHorizontalL,
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalS,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusLarge,
  },
  insightHeadline: {
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightSemibold,
  },
  badgeRow: {
    display: "flex",
    gap: tokens.spacingHorizontalXS,
    flexWrap: "wrap",
  },
  dataGrid: {
    marginTop: tokens.spacingVerticalM,
  },
  emptyState: {
    padding: tokens.spacingVerticalL,
    color: tokens.colorNeutralForeground3,
  },
});

type LoadState<T> =
  | { status: "loading" }
  | { status: "error"; error: string }
  | { status: "success"; data: T };

const currencyFormatter = new Intl.NumberFormat("en-US", {
  style: "currency",
  currency: "USD",
  maximumFractionDigits: 0,
});

const percentFormatter = new Intl.NumberFormat("en-US", {
  style: "percent",
  maximumFractionDigits: 1,
});

const compactPercentFormatter = new Intl.NumberFormat("en-US", {
  style: "percent",
  maximumFractionDigits: 0,
});

const dayFormatter = new Intl.NumberFormat("en-US", {
  maximumFractionDigits: 0,
});

const topSaverColumns: TableColumnDefinition<TopSaverItem>[] = [
  createTableColumn<TopSaverItem>({
    columnId: "title",
    renderHeaderCell: () => "Recommendation",
    renderCell: (item) => (
      <TableCellLayout>{item.title || "Untitled"}</TableCellLayout>
    ),
  }),
  createTableColumn<TopSaverItem>({
    columnId: "category",
    renderHeaderCell: () => "Category",
    renderCell: (item) => <TableCellLayout>{item.category}</TableCellLayout>,
  }),
  createTableColumn<TopSaverItem>({
    columnId: "monthlySavings",
    renderHeaderCell: () => "Monthly Savings",
    renderCell: (item) => (
      <TableCellLayout>
        {currencyFormatter.format(item.monthlySavings)}
      </TableCellLayout>
    ),
  }),
  createTableColumn<TopSaverItem>({
    columnId: "savingsReason",
    renderHeaderCell: () => "Reasoning",
    renderCell: (item) => (
      <TableCellLayout>
        {item.savingsReason || "No cost reasoning available for this estimate."}
      </TableCellLayout>
    ),
  }),
];

const costEvidenceColumns: TableColumnDefinition<CostEvidenceItem>[] = [
  createTableColumn<CostEvidenceItem>({
    columnId: "subscriptionId",
    renderHeaderCell: () => "Subscription",
    renderCell: (item) => (
      <TableCellLayout>{item.subscriptionId}</TableCellLayout>
    ),
  }),
  createTableColumn<CostEvidenceItem>({
    columnId: "resourceGroup",
    renderHeaderCell: () => "Resource group",
    renderCell: (item) => (
      <TableCellLayout>{item.resourceGroup || "All in scope"}</TableCellLayout>
    ),
  }),
  createTableColumn<CostEvidenceItem>({
    columnId: "monthToDateCostUsd",
    renderHeaderCell: () => "Month-to-date Cost",
    renderCell: (item) => (
      <TableCellLayout>
        {currencyFormatter.format(item.monthToDateCostUsd)}
      </TableCellLayout>
    ),
  }),
  createTableColumn<CostEvidenceItem>({
    columnId: "baselineMonthToDateCostUsd",
    renderHeaderCell: () => "Baseline MTD",
    renderCell: (item) => (
      <TableCellLayout>
        {currencyFormatter.format(item.baselineMonthToDateCostUsd ?? 0)}
      </TableCellLayout>
    ),
  }),
  createTableColumn<CostEvidenceItem>({
    columnId: "estimatedMonthlySavingsUsd",
    renderHeaderCell: () => "Estimated Savings",
    renderCell: (item) => (
      <TableCellLayout>
        {currencyFormatter.format(item.estimatedMonthlySavingsUsd ?? 0)}
      </TableCellLayout>
    ),
  }),
  createTableColumn<CostEvidenceItem>({
    columnId: "anomalyCount",
    renderHeaderCell: () => "Cost Alerts",
    renderCell: (item) => (
      <TableCellLayout>{item.anomalyCount}</TableCellLayout>
    ),
  }),
  createTableColumn<CostEvidenceItem>({
    columnId: "advisorRecommendationLinks",
    renderHeaderCell: () => "Advisor links",
    renderCell: (item) => (
      <TableCellLayout>{item.advisorRecommendationLinks ?? 0}</TableCellLayout>
    ),
  }),
  createTableColumn<CostEvidenceItem>({
    columnId: "activityLogCorrelationEvents",
    renderHeaderCell: () => "Activity correlations",
    renderCell: (item) => (
      <TableCellLayout>
        {item.activityLogCorrelationEvents ?? 0}
      </TableCellLayout>
    ),
  }),
  createTableColumn<CostEvidenceItem>({
    columnId: "evidenceSource",
    renderHeaderCell: () => "Source",
    renderCell: (item) => (
      <TableCellLayout>{item.evidenceSource}</TableCellLayout>
    ),
  }),
];

function getAccuracyBadge(accuracy: number) {
  if (accuracy >= 0.9) return { label: "High", color: "success" as const };
  if (accuracy >= 0.75) return { label: "Good", color: "warning" as const };
  return { label: "Low", color: "danger" as const };
}

function hasAzureMcpEvidence(costEvidence: CostEvidenceItem[] | undefined) {
  return (costEvidence ?? []).some((item) =>
    item.evidenceSource.toLowerCase().includes("azure mcp"),
  );
}

function getEvidenceCoverage(costEvidence: CostEvidenceItem[] | undefined) {
  const rows = costEvidence ?? [];
  if (rows.length === 0) return 0;
  const mcpRows = rows.filter((item) =>
    item.evidenceSource.toLowerCase().includes("azure mcp"),
  ).length;
  return mcpRows / rows.length;
}

function getEvidenceFreshnessDays(costEvidence: CostEvidenceItem[] | undefined) {
  const rows = costEvidence ?? [];
  if (rows.length === 0) return 0;

  const now = Date.now();
  const ages = rows
    .map((row) => {
      const ts = new Date(row.lastQueriedAt).getTime();
      if (Number.isNaN(ts)) return 0;
      return Math.max(0, (now - ts) / (1000 * 60 * 60 * 24));
    })
    .sort((a, b) => a - b);

  const middle = Math.floor(ages.length / 2);
  if (ages.length % 2 === 0) {
    return (ages[middle - 1] + ages[middle]) / 2;
  }

  return ages[middle];
}

function getEvidenceSignalScore(costEvidence: CostEvidenceItem[] | undefined) {
  const rows = costEvidence ?? [];
  if (rows.length === 0) return 0;

  const totals = rows.reduce(
    (acc, item) => {
      acc.anomalies += item.anomalyCount;
      acc.advisor += item.advisorRecommendationLinks ?? 0;
      acc.activity += item.activityLogCorrelationEvents ?? 0;
      return acc;
    },
    { anomalies: 0, advisor: 0, activity: 0 },
  );

  const density =
    (totals.advisor * 1.2 + totals.activity * 0.8 + totals.anomalies * 1.5) /
    rows.length;
  return Math.max(0, Math.min(100, Math.round(density * 10)));
}

function getHealthBadge(
  value: number,
  options: { high: number; medium: number },
) {
  if (value >= options.high) return { label: "Strong", color: "success" as const };
  if (value >= options.medium)
    return { label: "Moderate", color: "warning" as const };
  return { label: "Weak", color: "danger" as const };
}

export function ValueTrackingPage() {
  const styles = useStyles();
  const { accessToken } = useAccessToken();

  const [serviceGroupsState, setServiceGroupsState] = useState<
    LoadState<ServiceGroup[]>
  >({ status: "loading" });
  const [dashboardState, setDashboardState] = useState<
    LoadState<RoiDashboardData>
  >({ status: "loading" });
  const [selectedServiceGroupId, setSelectedServiceGroupId] =
    useState<string>("");
  const serviceGroupLabelId = useId("service-group-label");

  useEffect(() => {
    let isActive = true;

    controlPlaneApi
      .listServiceGroups(accessToken ?? undefined)
      .then((result) => {
        if (!isActive) return;
        setServiceGroupsState({ status: "success", data: result.value ?? [] });
      })
      .catch((error: unknown) => {
        if (!isActive) return;
        const message =
          error instanceof Error
            ? error.message
            : "Unable to load service groups";
        setServiceGroupsState({ status: "error", error: message });
      });

    return () => {
      isActive = false;
    };
  }, [accessToken]);

  useEffect(() => {
    let isActive = true;

    const serviceGroupId = selectedServiceGroupId || undefined;

    controlPlaneApi
      .getValueTrackingDashboard(serviceGroupId, accessToken ?? undefined)
      .then((result) => {
        if (!isActive) return;
        setDashboardState({ status: "success", data: result });
      })
      .catch((error: unknown) => {
        if (!isActive) return;
        const message =
          error instanceof Error
            ? error.message
            : "Unable to load value tracking dashboard";
        setDashboardState({ status: "error", error: message });
      });

    return () => {
      isActive = false;
    };
  }, [accessToken, selectedServiceGroupId]);

  const serviceGroups =
    serviceGroupsState.status === "success" ? serviceGroupsState.data : [];

  const dashboard =
    dashboardState.status === "success" ? dashboardState.data : null;

  const currentRunRate = dashboard?.currentAnnualRunRate ?? 0;
  const optimisedTarget = dashboard?.optimisedAnnualRunRate ?? 0;

  const dedupedTopSavers = useMemo(() => {
    if (!dashboard) return [];

    const byRecommendation = new Map<string, TopSaverItem>();
    for (const item of dashboard.topSavers) {
      const existing = byRecommendation.get(item.recommendationId);
      if (!existing || item.monthlySavings > existing.monthlySavings) {
        byRecommendation.set(item.recommendationId, item);
      }
    }

    return Array.from(byRecommendation.values())
      .sort((a, b) => b.monthlySavings - a.monthlySavings)
      .slice(0, 5);
  }, [dashboard]);

  const accuracyBadge = useMemo(() => {
    if (!dashboard) return null;
    return getAccuracyBadge(dashboard.savingsAccuracy / 100);
  }, [dashboard]);

  const usingAzureMcpEvidence = useMemo(
    () => hasAzureMcpEvidence(dashboard?.costEvidence),
    [dashboard?.costEvidence],
  );

  const evidenceCoverage = useMemo(
    () => getEvidenceCoverage(dashboard?.costEvidence),
    [dashboard?.costEvidence],
  );

  const evidenceFreshnessDays = useMemo(
    () => getEvidenceFreshnessDays(dashboard?.costEvidence),
    [dashboard?.costEvidence],
  );

  const evidenceSignalScore = useMemo(
    () => getEvidenceSignalScore(dashboard?.costEvidence),
    [dashboard?.costEvidence],
  );

  const realizationRate = useMemo(() => {
    if (!dashboard || dashboard.totalEstimatedAnnualSavings <= 0) return 0;
    return Math.max(
      0,
      dashboard.totalActualAnnualSavings / dashboard.totalEstimatedAnnualSavings,
    );
  }, [dashboard]);

  const annualSavingsGap = useMemo(() => {
    if (!dashboard) return 0;
    return dashboard.totalEstimatedAnnualSavings - dashboard.totalActualAnnualSavings;
  }, [dashboard]);

  const annualOpportunityGap = useMemo(() => {
    if (!dedupedTopSavers.length) return 0;
    return dedupedTopSavers.reduce((acc, saver) => acc + saver.monthlySavings, 0) * 12;
  }, [dedupedTopSavers]);

  const evidenceCoverageBadge = useMemo(
    () => getHealthBadge(evidenceCoverage, { high: 0.8, medium: 0.5 }),
    [evidenceCoverage],
  );

  const evidenceSignalBadge = useMemo(
    () => getHealthBadge(evidenceSignalScore, { high: 70, medium: 45 }),
    [evidenceSignalScore],
  );

  return (
    <div className={styles.container}>
      <div className={styles.headerRow}>
        <AzurePageHeader
          title="Total Cost of Ownership"
          subtitle="Understand current spend, savings opportunities, and realized value"
        />
        <div className={styles.filterRow}>
          <Label id={serviceGroupLabelId}>Service group</Label>
          <Dropdown
            aria-labelledby={serviceGroupLabelId}
            value={
              selectedServiceGroupId
                ? (serviceGroups.find((sg) => sg.id === selectedServiceGroupId)
                    ?.name ?? "")
                : "All service groups"
            }
            placeholder="All service groups"
            onOptionSelect={(_, data) => {
              const nextId = String(data.optionValue ?? "");
              setDashboardState({ status: "loading" });
              setSelectedServiceGroupId(nextId);
            }}
            disabled={serviceGroupsState.status === "loading"}
            style={{ minWidth: 220 }}
          >
            <Option value="">All service groups</Option>
            {serviceGroups.map((group) => (
              <Option key={group.id} value={group.id}>
                {group.name}
              </Option>
            ))}
          </Dropdown>
        </div>
      </div>

      {dashboardState.status === "loading" && (
        <Card className={styles.sectionCard}>
          <Spinner label="Loading value tracking dashboard" />
        </Card>
      )}

      {dashboardState.status === "error" && (
        <Card className={styles.sectionCard} role="alert" aria-live="assertive">
          <Text weight="semibold">Unable to load dashboard</Text>
          <Text style={{ color: tokens.colorNeutralForeground3 }}>
            {dashboardState.error}
          </Text>
        </Card>
      )}

      {dashboard && (
        <>
          <div className={styles.metricsGrid}>
            <Card className={styles.metricCard}>
              <CardHeader
                header={
                  <Text className={styles.metricValue}>
                    {currentRunRate > 0
                      ? currencyFormatter.format(currentRunRate)
                      : "—"}
                  </Text>
                }
                description={
                  <Text className={styles.metricLabel}>
                    Current annual run rate
                  </Text>
                }
              />
            </Card>

            <Card className={styles.metricCard}>
              <CardHeader
                header={
                  <Text className={styles.metricValue}>
                    {currencyFormatter.format(
                      dashboard.totalEstimatedAnnualSavings,
                    )}
                  </Text>
                }
                description={
                  <Text className={styles.metricLabel}>
                    Savings opportunity
                  </Text>
                }
              />
            </Card>

            <Card className={styles.metricCard}>
              <CardHeader
                header={
                  <Text className={styles.metricValue}>
                    {currentRunRate > 0
                      ? currencyFormatter.format(optimisedTarget)
                      : "—"}
                  </Text>
                }
                description={
                  <Text className={styles.metricLabel}>
                    Optimised annual target
                  </Text>
                }
              />
            </Card>

            <Card className={styles.metricCard}>
              <CardHeader
                header={
                  <Text className={styles.metricValue}>
                    {currencyFormatter.format(
                      dashboard.totalActualAnnualSavings,
                    )}
                  </Text>
                }
                description={
                  <Text className={styles.metricLabel}>
                    Realized savings
                  </Text>
                }
              />
            </Card>

            <Card className={styles.metricCard}>
              <CardHeader
                header={
                  <Text className={styles.metricValue}>
                    {percentFormatter.format(dashboard.savingsAccuracy / 100)}
                  </Text>
                }
                description={
                  <Text className={styles.metricLabel}>Savings accuracy</Text>
                }
                action={
                  accuracyBadge ? (
                    <Badge appearance="tint" color={accuracyBadge.color}>
                      {accuracyBadge.label}
                    </Badge>
                  ) : null
                }
              />
            </Card>

            <Card className={styles.metricCard}>
              <CardHeader
                header={
                  <Text className={styles.metricValue}>
                    {dayFormatter.format(dashboard.averagePaybackDays)}
                  </Text>
                }
                description={
                  <Text className={styles.metricLabel}>Avg. payback days</Text>
                }
              />
            </Card>

            <Card className={styles.metricCard}>
              <CardHeader
                header={
                  <Text className={styles.metricValue}>
                    {dashboard.paybackAchievedCount}
                  </Text>
                }
                description={
                  <Text className={styles.metricLabel}>Payback achieved</Text>
                }
              />
            </Card>

            <Card className={styles.metricCard}>
              <CardHeader
                header={
                  <Text className={styles.metricValue}>
                    {dashboard.totalRecommendations}
                  </Text>
                }
                description={
                  <Text className={styles.metricLabel}>Tracked recs</Text>
                }
              />
            </Card>

            <Card className={styles.metricCard}>
              <CardHeader
                header={
                  <Text className={styles.metricValue}>
                    {compactPercentFormatter.format(realizationRate)}
                  </Text>
                }
                description={
                  <Text className={styles.metricLabel}>Realization rate</Text>
                }
                action={
                  <Badge appearance="tint" color={realizationRate >= 0.75 ? "success" : realizationRate >= 0.4 ? "warning" : "danger"}>
                    {realizationRate >= 0.75
                      ? "On track"
                      : realizationRate >= 0.4
                        ? "Needs momentum"
                        : "At risk"}
                  </Badge>
                }
              />
            </Card>

            <Card className={styles.metricCard}>
              <CardHeader
                header={
                  <Text className={styles.metricValue}>
                    {compactPercentFormatter.format(evidenceCoverage)}
                  </Text>
                }
                description={
                  <Text className={styles.metricLabel}>MCP evidence coverage</Text>
                }
                action={
                  <Badge appearance="tint" color={evidenceCoverageBadge.color}>
                    {evidenceCoverageBadge.label}
                  </Badge>
                }
              />
            </Card>
          </div>

          <Card className={styles.sectionCard}>
            <Text size={500} weight="semibold">
              Cost analysis
            </Text>
            <Text className={styles.insightText}>
              Use this section to understand where your spend is going, what you
              can save, and how to close the gap between opportunity and realized
              value.
            </Text>
            <div className={styles.insightsGrid}>
              <div className={styles.insightCard}>
                <Text className={styles.insightHeadline}>Savings gap</Text>
                <Text>
                  {annualSavingsGap > 0
                    ? `${currencyFormatter.format(annualSavingsGap)}/yr in identified savings is not yet realized. Initialize value tracking to start measuring actuals.`
                    : "Realized savings meets or exceeds current estimates for this scope."}
                </Text>
                <div className={styles.badgeRow}>
                  <Badge appearance="outline" color={annualSavingsGap > 0 ? "warning" : "success"}>
                    {annualSavingsGap > 0 ? "Close estimate gap" : "Estimate met"}
                  </Badge>
                  <Badge appearance="outline" color="brand">
                    {compactPercentFormatter.format(realizationRate)} realized
                  </Badge>
                </div>
              </div>

              <div className={styles.insightCard}>
                <Text className={styles.insightHeadline}>Evidence quality</Text>
                <Text>
                  Median evidence freshness is {dayFormatter.format(evidenceFreshnessDays)} day(s) with a signal score of {evidenceSignalScore}/100.
                </Text>
                <div className={styles.badgeRow}>
                  <Badge appearance="tint" color={evidenceCoverageBadge.color}>
                    Coverage {compactPercentFormatter.format(evidenceCoverage)}
                  </Badge>
                  <Badge appearance="tint" color={evidenceSignalBadge.color}>
                    Signal {evidenceSignalScore}/100
                  </Badge>
                </div>
              </div>

              <div className={styles.insightCard}>
                <Text className={styles.insightHeadline}>Near-term opportunity</Text>
                <Text>
                  Top saver pipeline represents up to {currencyFormatter.format(annualOpportunityGap)} annualized opportunity if execution remains consistent.
                </Text>
                <div className={styles.badgeRow}>
                  <Badge appearance="outline" color="success">
                    {dedupedTopSavers.length} active saver(s)
                  </Badge>
                  <Badge appearance="outline" color="informative">
                    Prioritize FinOps + high-confidence items
                  </Badge>
                </div>
              </div>
            </div>
          </Card>

          <Card className={styles.sectionCard}>
            <Text size={500} weight="semibold">
              Cost reduction opportunities
            </Text>
            <Text className={styles.insightText}>
              Ranked by estimated monthly savings per recommendation. Duplicates
              are automatically collapsed to keep this list actionable.
            </Text>
            {dedupedTopSavers.length === 0 ? (
              <div className={styles.emptyState}>
                No savings-bearing recommendations found. Run an assessment to
                generate cost reduction opportunities.
              </div>
            ) : (
              <DataGrid
                items={dedupedTopSavers}
                columns={topSaverColumns}
                className={styles.dataGrid}
                sortable
              >
                <DataGridHeader>
                  <DataGridRow>
                    {({ renderHeaderCell }) => (
                      <DataGridHeaderCell>
                        {renderHeaderCell()}
                      </DataGridHeaderCell>
                    )}
                  </DataGridRow>
                </DataGridHeader>
                <DataGridBody<TopSaverItem>>
                  {({ item, rowId }) => (
                    <DataGridRow<TopSaverItem> key={rowId}>
                      {({ renderCell }) => (
                        <DataGridCell>{renderCell(item)}</DataGridCell>
                      )}
                    </DataGridRow>
                  )}
                </DataGridBody>
              </DataGrid>
            )}
          </Card>

          <Card className={styles.sectionCard}>
            <Text size={500} weight="semibold">
              Billing API evidence
            </Text>
            <Text style={{ color: tokens.colorNeutralForeground3 }}>
              {dashboard.billingEvidenceStatus ??
                "Billing evidence unavailable for this view."}
            </Text>
            <Text className={styles.insightText}>
              {dashboard.azureMcpToolCallStatus ??
                "Azure MCP tool-call status is not available for this view."}
            </Text>
            <Text className={styles.insightText}>
              {usingAzureMcpEvidence
                ? "Azure MCP evidence is available for this scope, so estimated savings can be reconciled with richer operational context."
                : "This view is currently operating on a coarse billing fallback. It is useful for directional ROI, but not for recommendation-specific proof of realized value."}
            </Text>
            <Text className={styles.insightText}>
              Evidence coverage: {compactPercentFormatter.format(evidenceCoverage)} • median freshness: {dayFormatter.format(evidenceFreshnessDays)} day(s) • signal score: {evidenceSignalScore}/100.
            </Text>
            {(dashboard.costEvidence?.length ?? 0) > 0 ? (
              <DataGrid
                items={dashboard.costEvidence ?? []}
                columns={costEvidenceColumns}
                className={styles.dataGrid}
              >
                <DataGridHeader>
                  <DataGridRow>
                    {({ renderHeaderCell }) => (
                      <DataGridHeaderCell>
                        {renderHeaderCell()}
                      </DataGridHeaderCell>
                    )}
                  </DataGridRow>
                </DataGridHeader>
                <DataGridBody<CostEvidenceItem>>
                  {({ item, rowId }) => (
                    <DataGridRow<CostEvidenceItem> key={rowId}>
                      {({ renderCell }) => (
                        <DataGridCell>{renderCell(item)}</DataGridCell>
                      )}
                    </DataGridRow>
                  )}
                </DataGridBody>
              </DataGrid>
            ) : (
              <div className={styles.emptyState}>
                No Billing API evidence to display yet.
              </div>
            )}
          </Card>
        </>
      )}
    </div>
  );
}
