import React, { useCallback, useEffect, useMemo, useState } from "react";
import { useNavigate } from "react-router-dom";
import {
  makeStyles,
  mergeClasses,
  tokens,
  Text,
  Button,
  Card,
  CardHeader,
  Badge,
  Select,
  Spinner,
  Tab,
  TabList,
  DataGrid,
  DataGridHeader,
  DataGridRow,
  DataGridHeaderCell,
  DataGridBody,
  DataGridCell,
  createTableColumn,
  type TableColumnDefinition,
  TableCellLayout,
  Dropdown,
  Option,
} from "@fluentui/react-components";
import {
  Timeline24Regular,
  Warning24Regular,
  CheckmarkCircle24Regular,
  Filter24Regular,
} from "@fluentui/react-icons";
import {
  LineChart,
  Line,
  XAxis,
  YAxis,
  CartesianGrid,
  Tooltip,
  Legend,
  ResponsiveContainer,
  Area,
  AreaChart,
} from "recharts";
import { controlPlaneApi } from "../services/controlPlaneApi";
import { log } from "../telemetry/logger";
import type { Recommendation, ServiceGroup } from "../services/controlPlaneApi";
import { useAccessToken } from "../auth/useAccessToken";
import { useNotify } from "../components/useNotify";
import { useDriftData, type DayOption } from "../hooks/useDriftData";
import { DriftTypeSummaryCards } from "../components/DriftTypeSummaryCards";

const useStyles = makeStyles({
  container: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalL,
    padding: tokens.spacingVerticalXL,
  },
  header: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "center",
  },
  headerTitle: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalM,
  },
  filters: {
    display: "flex",
    gap: tokens.spacingHorizontalM,
    alignItems: "center",
  },
  metricsGrid: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(240px, 1fr))",
    gap: tokens.spacingVerticalM,
  },
  metricCard: {
    height: "120px",
  },
  metricValue: {
    fontSize: tokens.fontSizeHero900,
    fontWeight: tokens.fontWeightSemibold,
    lineHeight: tokens.lineHeightHero900,
  },
  metricLabel: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase300,
  },
  trendBadge: {
    marginTop: tokens.spacingVerticalS,
  },
  chartCard: {
    padding: tokens.spacingVerticalL,
  },
  chartContainer: {
    height: "300px",
    marginTop: tokens.spacingVerticalM,
  },
  matrixContainer: {
    marginTop: tokens.spacingVerticalL,
  },
  matrixGrid: {
    display: "grid",
    gridTemplateColumns: "repeat(4, 1fr)",
    gap: tokens.spacingVerticalS,
    marginTop: tokens.spacingVerticalM,
  },
  matrixCell: {
    padding: tokens.spacingVerticalM,
    textAlign: "center",
    borderRadius: tokens.borderRadiusMedium,
    cursor: "pointer",
    transition: "all 0.2s ease",
    ":hover": {
      transform: "scale(1.02)",
      boxShadow: tokens.shadow8,
    },
  },
  criticalCell: {
    backgroundColor: tokens.colorPaletteRedBackground2,
    border: `2px solid ${tokens.colorPaletteRedBorder2}`,
  },
  highCell: {
    backgroundColor: tokens.colorPaletteMarigoldBackground2,
    border: `2px solid ${tokens.colorPaletteMarigoldBorder2}`,
  },
  mediumCell: {
    backgroundColor: tokens.colorPaletteYellowBackground2,
    border: `2px solid ${tokens.colorPaletteYellowBorder2}`,
  },
  lowCell: {
    backgroundColor: tokens.colorPaletteLightGreenBackground2,
    border: `2px solid ${tokens.colorPaletteLightGreenBorder2}`,
  },
  violationList: {
    marginTop: tokens.spacingVerticalL,
  },
  emptyState: {
    textAlign: "center",
    padding: `${tokens.spacingVerticalXXXL} ${tokens.spacingHorizontalXXXL}`,
    color: tokens.colorNeutralForeground3,
  },
  // ─── Calendar heatmap styles ───────────────────────────────────────────────
  heatmapCard: {
    padding: tokens.spacingVerticalL,
    overflow: "hidden",
  },
  heatmapScroll: {
    overflowX: "auto",
    paddingBottom: tokens.spacingVerticalS,
  },
  heatmapContainer: {
    display: "flex",
    alignItems: "flex-start",
    gap: "4px",
  },
  heatmapDayLabels: {
    display: "flex",
    flexDirection: "column",
    gap: "2px",
    paddingTop: "20px",
    flexShrink: 0,
  },
  heatmapDayLabel: {
    height: "14px",
    lineHeight: "14px",
    fontSize: "10px",
    color: tokens.colorNeutralForeground3,
    width: "28px",
    textAlign: "right",
    paddingRight: "4px",
  },
  heatmapWeeksArea: {
    display: "flex",
    flexDirection: "column",
    gap: "2px",
  },
  heatmapMonthRow: {
    position: "relative",
    height: "16px",
  },
  heatmapMonthLabel: {
    position: "absolute",
    fontSize: "10px",
    color: tokens.colorNeutralForeground3,
    top: 0,
    whiteSpace: "nowrap",
  },
  heatmapWeeks: {
    display: "flex",
    gap: "2px",
  },
  heatmapWeek: {
    display: "flex",
    flexDirection: "column",
    gap: "2px",
  },
  heatmapCell: {
    width: "14px",
    height: "14px",
    borderRadius: "2px",
    flexShrink: 0,
  },
  heatmapCellOor: {
    backgroundColor: "transparent",
    pointerEvents: "none",
  },
  heatmapCellL0: {
    backgroundColor: tokens.colorNeutralBackground4,
  },
  heatmapCellL1: {
    backgroundColor: tokens.colorPaletteLightGreenBackground2,
  },
  heatmapCellL2: {
    backgroundColor: tokens.colorPaletteYellowBackground2,
  },
  heatmapCellL3: {
    backgroundColor: tokens.colorPaletteMarigoldBackground2,
  },
  heatmapCellL4: {
    backgroundColor: tokens.colorPaletteRedBackground2,
  },
  heatmapLegend: {
    display: "flex",
    alignItems: "center",
    gap: "4px",
    marginTop: tokens.spacingVerticalS,
  },
  heatmapLegendText: {
    fontSize: "10px",
    color: tokens.colorNeutralForeground3,
  },
});

// ─── DriftCalendarHeatmap ──────────────────────────────────────────────────────

type DriftHeatmapSnapshot = {
  snapshotTime: string;
  totalViolations: number;
  criticalViolations: number;
};

/**
 * GitHub-contribution-style calendar heatmap of drift violations per day.
 * Columns = weeks; rows = day of week (Sun..Sat). Color intensity reflects
 * the peak violation count recorded on that day.
 */
function DriftCalendarHeatmap({
  snapshots,
  timeRange,
}: {
  snapshots: DriftHeatmapSnapshot[];
  timeRange: DayOption;
}) {
  const styles = useStyles();

  const cellsByDate = useMemo(() => {
    const map = new Map<string, number>();
    for (const s of snapshots) {
      const key = new Date(s.snapshotTime).toISOString().slice(0, 10);
      map.set(key, Math.max(map.get(key) ?? 0, s.totalViolations));
    }
    return map;
  }, [snapshots]);

  const { startDate, endDate, maxViolations, weeks, monthLabels } =
    useMemo(() => {
      const end = new Date();
      end.setHours(23, 59, 59, 999);
      const start = new Date();
      start.setDate(start.getDate() - Number(timeRange));
      start.setHours(0, 0, 0, 0);

      const max = Math.max(...Array.from(cellsByDate.values()), 1);

      // Align grid to Sunday before startDate
      const gridStart = new Date(start);
      gridStart.setDate(gridStart.getDate() - gridStart.getDay());
      gridStart.setHours(0, 0, 0, 0);

      const weeksArr: Date[][] = [];
      const curr = new Date(gridStart);
      while (curr <= end) {
        const week: Date[] = [];
        for (let d = 0; d < 7; d++) {
          week.push(new Date(curr));
          curr.setDate(curr.getDate() + 1);
        }
        weeksArr.push(week);
      }

      const labels: { weekIdx: number; label: string }[] = [];
      weeksArr.forEach((week, i) => {
        const first = week[0];
        if (first.getDate() <= 7 || i === 0) {
          labels.push({
            weekIdx: i,
            label: first.toLocaleDateString(undefined, { month: "short" }),
          });
        }
      });

      return {
        startDate: start,
        endDate: end,
        maxViolations: max,
        weeks: weeksArr,
        monthLabels: labels,
      };
    }, [cellsByDate, timeRange]);

  function getCellClass(date: Date): string {
    if (date < startDate || date > endDate) {
      return mergeClasses(styles.heatmapCell, styles.heatmapCellOor);
    }
    const key = date.toISOString().slice(0, 10);
    const v = cellsByDate.get(key);
    if (v === undefined || v === 0) {
      return mergeClasses(styles.heatmapCell, styles.heatmapCellL0);
    }
    const ratio = v / maxViolations;
    if (ratio < 0.25)
      return mergeClasses(styles.heatmapCell, styles.heatmapCellL1);
    if (ratio < 0.5)
      return mergeClasses(styles.heatmapCell, styles.heatmapCellL2);
    if (ratio < 0.75)
      return mergeClasses(styles.heatmapCell, styles.heatmapCellL3);
    return mergeClasses(styles.heatmapCell, styles.heatmapCellL4);
  }

  const DAY_LABELS = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
  const CELL_WIDTH = 16; // 14px cell + 2px gap

  return (
    <div>
      <div className={styles.heatmapScroll}>
        <div className={styles.heatmapContainer}>
          {/* Day-of-week labels */}
          <div className={styles.heatmapDayLabels}>
            {DAY_LABELS.map((d) => (
              <div key={d} className={styles.heatmapDayLabel}>
                {d}
              </div>
            ))}
          </div>

          {/* Calendar grid */}
          <div className={styles.heatmapWeeksArea}>
            {/* Month labels */}
            <div
              className={styles.heatmapMonthRow}
              style={{ width: `${weeks.length * CELL_WIDTH}px` }}
            >
              {monthLabels.map(({ weekIdx, label }) => (
                <span
                  key={label + weekIdx}
                  className={styles.heatmapMonthLabel}
                  style={{ left: `${weekIdx * CELL_WIDTH}px` }}
                >
                  {label}
                </span>
              ))}
            </div>

            {/* Week columns */}
            <div className={styles.heatmapWeeks}>
              {weeks.map((week, wi) => (
                // biome-ignore lint/suspicious/noArrayIndexKey: stable week order based on calendar position
                <div key={wi} className={styles.heatmapWeek}>
                  {week.map((day, di) => {
                    const violations =
                      cellsByDate.get(day.toISOString().slice(0, 10)) ?? 0;
                    const inRange = day >= startDate && day <= endDate;
                    return (
                      <div
                        // biome-ignore lint/suspicious/noArrayIndexKey: stable day position within week
                        key={di}
                        className={getCellClass(day)}
                        title={
                          inRange
                            ? `${day.toLocaleDateString()}: ${violations} violation${violations !== 1 ? "s" : ""}`
                            : undefined
                        }
                        role={inRange ? "img" : undefined}
                        aria-label={
                          inRange
                            ? `${day.toLocaleDateString()}: ${violations} violations`
                            : undefined
                        }
                      />
                    );
                  })}
                </div>
              ))}
            </div>
          </div>
        </div>
      </div>

      {/* Legend */}
      <div className={styles.heatmapLegend}>
        <span className={styles.heatmapLegendText}>Fewer</span>
        {(
          [
            "heatmapCellL0",
            "heatmapCellL1",
            "heatmapCellL2",
            "heatmapCellL3",
            "heatmapCellL4",
          ] as const
        ).map((cls) => (
          <div
            key={cls}
            className={mergeClasses(styles.heatmapCell, styles[cls])}
            aria-hidden="true"
          />
        ))}
        <span className={styles.heatmapLegendText}>More violations</span>
      </div>
    </div>
  );
}

interface Violation {
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

export const DriftTimelinePage: React.FC = () => {
  const styles = useStyles();
  const navigate = useNavigate();
  const { accessToken } = useAccessToken();
  const notify = useNotify();

  const [loadingServiceGroups, setLoadingServiceGroups] = useState(true);
  const [serviceGroups, setServiceGroups] = useState<ServiceGroup[]>([]);
  const [selectedServiceGroupId, setSelectedServiceGroupId] =
    useState<string>("");
  const [timeRange, setTimeRange] = useState<DayOption>("30");
  const [selectedTab, setSelectedTab] = useState("timeline");
  const [driftCategoryFilter, setDriftCategoryFilter] = useState<string | null>(
    null,
  );
  const [errorServiceGroups, setErrorServiceGroups] = useState<string | null>(
    null,
  );
  const [linkedRecommendations, setLinkedRecommendations] = useState<
    Recommendation[]
  >([]);

  // Use custom hook for drift data fetching
  const {
    snapshots,
    violations,
    currentSnapshot,
    trendDirection,
    scoreChange,
    loading: loadingData,
    error: driftError,
    refresh: refreshDriftData,
  } = useDriftData(selectedServiceGroupId, timeRange, accessToken);

  const error = errorServiceGroups || driftError;

  const handleDriftCategoryClick = useCallback((category: string) => {
    setDriftCategoryFilter((prev) => (prev === category ? null : category));
    setSelectedTab("violations");
  }, []);

  useEffect(() => {
    document.title = "NimbusIQ — Drift Analysis";
  }, []);

  // Load service groups on mount
  useEffect(() => {
    async function fetchServiceGroups() {
      setLoadingServiceGroups(true);
      try {
        const res = await controlPlaneApi.listServiceGroups(
          accessToken ?? undefined,
        );
        setServiceGroups(res.value ?? []);
        if ((res.value ?? []).length > 0) {
          setSelectedServiceGroupId((res.value ?? [])[0].id);
        }
      } catch (err: unknown) {
        log.error("Failed to load service groups", { error: err });
        setErrorServiceGroups("Failed to load service groups");
        notify({
          title: "Failed to load service groups",
          body: err instanceof Error ? err.message : String(err),
          intent: "error",
        });
      } finally {
        setLoadingServiceGroups(false);
      }
    }
    void fetchServiceGroups();
  }, [accessToken, notify]);

  const chartData = snapshots.map((s) => ({
    date: new Date(s.snapshotTime).toLocaleDateString(),
    driftScore: s.driftScore,
    critical: s.criticalViolations,
    high: s.highViolations,
    medium: s.mediumViolations,
    low: s.lowViolations,
    total: s.totalViolations,
  }));

  const selectedServiceGroup = serviceGroups.find(
    (sg) => sg.id === selectedServiceGroupId,
  );

  const columns: TableColumnDefinition<Violation>[] = [
    createTableColumn<Violation>({
      columnId: "severity",
      renderHeaderCell: () => "Severity",
      renderCell: (item) => (
        <TableCellLayout>
          <Badge
            appearance={
              item.severity === "Critical"
                ? "filled"
                : item.severity === "High"
                  ? "tint"
                  : "outline"
            }
            color={
              item.severity === "Critical"
                ? "danger"
                : item.severity === "High"
                  ? "warning"
                  : "informative"
            }
          >
            {item.severity}
          </Badge>
        </TableCellLayout>
      ),
    }),
    createTableColumn<Violation>({
      columnId: "rule",
      renderHeaderCell: () => "Rule",
      renderCell: (item) => <TableCellLayout>{item.ruleName}</TableCellLayout>,
    }),
    createTableColumn<Violation>({
      columnId: "category",
      renderHeaderCell: () => "Category",
      renderCell: (item) => <TableCellLayout>{item.category}</TableCellLayout>,
    }),
    createTableColumn<Violation>({
      columnId: "resource",
      renderHeaderCell: () => "Resource",
      renderCell: (item) => (
        <TableCellLayout truncate title={item.resourceId}>
          {item.resourceId.split("/").pop()}
        </TableCellLayout>
      ),
    }),
    createTableColumn<Violation>({
      columnId: "status",
      renderHeaderCell: () => "Status",
      renderCell: (item) => (
        <TableCellLayout>
          <Badge
            appearance="outline"
            color={item.status === "resolved" ? "success" : "warning"}
          >
            {item.status}
          </Badge>
        </TableCellLayout>
      ),
    }),
    createTableColumn<Violation>({
      columnId: "detectedAt",
      renderHeaderCell: () => "Detected",
      renderCell: (item) => (
        <TableCellLayout>
          {new Date(item.detectedAt).toLocaleDateString()}
        </TableCellLayout>
      ),
    }),
  ];

  useEffect(() => {
    let cancelled = false;

    if (!selectedServiceGroupId) {
      setLinkedRecommendations([]);
      return;
    }

    void controlPlaneApi
      .listRecommendations(
        {
          serviceGroupId: selectedServiceGroupId,
          status: "pending,pending_approval,manual_review",
          orderBy: "riskweighted",
          limit: 10,
        },
        accessToken,
      )
      .then((result) => {
        if (!cancelled) {
          setLinkedRecommendations(result.value ?? []);
        }
      })
      .catch(() => {
        if (!cancelled) {
          setLinkedRecommendations([]);
        }
      });

    return () => {
      cancelled = true;
    };
  }, [accessToken, selectedServiceGroupId]);

  if (loadingServiceGroups) {
    return (
      <div className={styles.container}>
        <Spinner label="Loading service groups..." />
      </div>
    );
  }

  if (serviceGroups.length === 0) {
    return (
      <div className={styles.container}>
        <div className={styles.emptyState}>
          <Timeline24Regular
            style={{ fontSize: "48px", marginBottom: tokens.spacingVerticalM }}
          />
          <Text size={500} weight="semibold" block>
            No service groups found
          </Text>
          <Text size={300} block style={{ marginTop: tokens.spacingVerticalS }}>
            Discover service groups first, then run an analysis to generate
            drift data.
          </Text>
        </div>
      </div>
    );
  }

  return (
    <div className={styles.container}>
      {/* Header */}
      <div className={styles.header}>
        <div className={styles.headerTitle}>
          <Timeline24Regular />
          <Text size={600} weight="semibold">
            Drift Timeline &amp; Remediation
          </Text>
        </div>
        <div className={styles.filters}>
          {/* Service group selector */}
          <Dropdown
            value={selectedServiceGroup?.name ?? "Select service group"}
            onOptionSelect={(_, data) =>
              setSelectedServiceGroupId(data.optionValue ?? "")
            }
            aria-label="Select service group"
          >
            {serviceGroups.map((sg) => (
              <Option key={sg.id} value={sg.id}>
                {sg.name}
              </Option>
            ))}
          </Dropdown>

          <Select
            value={timeRange}
            onChange={(_, data) => setTimeRange(data.value as DayOption)}
            aria-label="Time range"
          >
            <option value="7">Last 7 days</option>
            <option value="30">Last 30 days</option>
            <option value="90">Last 90 days</option>
          </Select>
          <Button
            appearance="subtle"
            icon={<Filter24Regular />}
            onClick={() => refreshDriftData()}
            disabled={loadingData}
          >
            Refresh
          </Button>
        </div>
      </div>

      {error && (
        <Card>
          <Text style={{ color: tokens.colorPaletteRedForeground1 }}>
            {error}
          </Text>
        </Card>
      )}

      {loadingData && <Spinner label="Loading drift data..." />}

      {!loadingData && !currentSnapshot && !error && (
        <div className={styles.emptyState}>
          <Timeline24Regular
            style={{ fontSize: "48px", marginBottom: tokens.spacingVerticalM }}
          />
          <Text size={500} weight="semibold" block>
            No drift data yet
          </Text>
          <Text size={300} block style={{ marginTop: tokens.spacingVerticalS }}>
            Run an analysis on this service group first to generate drift
            snapshots.
          </Text>
        </div>
      )}

      {!loadingData && currentSnapshot && (
        <>
          {(currentSnapshot.causeType || currentSnapshot.causeSource) && (
            <Card>
              <CardHeader
                header={<Text weight="semibold">Likely Drift Cause</Text>}
                description={
                  <Text size={200}>
                    Evidence-derived causal hint from nearby audit activity.
                  </Text>
                }
              />
              <div
                style={{
                  display: "flex",
                  gap: tokens.spacingHorizontalS,
                  flexWrap: "wrap",
                }}
              >
                {currentSnapshot.causeType && (
                  <Badge appearance="filled" color="warning">
                    {currentSnapshot.causeType}
                  </Badge>
                )}
                {currentSnapshot.causeSource && (
                  <Badge appearance="outline">
                    {currentSnapshot.causeSource}
                  </Badge>
                )}
                {typeof currentSnapshot.causeIsAuthoritative === "boolean" && (
                  <Badge
                    appearance="filled"
                    color={
                      currentSnapshot.causeIsAuthoritative
                        ? "success"
                        : "informative"
                    }
                  >
                    {currentSnapshot.causeIsAuthoritative
                      ? "Confirmed (Activity Log)"
                      : "Inferred"}
                  </Badge>
                )}
                {currentSnapshot.causeActor && (
                  <Badge appearance="outline">
                    Actor: {currentSnapshot.causeActor}
                  </Badge>
                )}
                {typeof currentSnapshot.causeConfidence === "number" && (
                  <Badge appearance="tint" color="brand">
                    Confidence{" "}
                    {(currentSnapshot.causeConfidence * 100).toFixed(0)}%
                  </Badge>
                )}
              </div>
              {currentSnapshot.causeEventTime && (
                <Text size={200} style={{ marginTop: tokens.spacingVerticalS }}>
                  Event time:{" "}
                  {new Date(currentSnapshot.causeEventTime).toLocaleString()}
                </Text>
              )}
              {currentSnapshot.causeEventId && (
                <Text
                  size={200}
                  style={{ color: tokens.colorNeutralForeground3 }}
                >
                  Correlation: {currentSnapshot.causeEventId}
                </Text>
              )}
              <Text size={200} style={{ marginTop: tokens.spacingVerticalS }}>
                <strong>Likely remediation path:</strong>{" "}
                {deriveDriftRemediationGuidance(currentSnapshot.causeType)}
              </Text>
              {linkedRecommendations[0] && (
                <div
                  style={{
                    display: "flex",
                    flexDirection: "column",
                    gap: tokens.spacingVerticalXS,
                    marginTop: tokens.spacingVerticalS,
                  }}
                >
                  <Text size={200}>
                    <strong>Linked recommendation:</strong>{" "}
                    {linkedRecommendations[0].title ??
                      linkedRecommendations[0].recommendationType}
                  </Text>
                  <Button
                    appearance="secondary"
                    size="small"
                    onClick={() =>
                      navigate(`/recommendations/${linkedRecommendations[0].id}`)
                    }
                  >
                    Open linked recommendation
                  </Button>
                </div>
              )}
            </Card>
          )}

          {/* Metrics Cards */}
          <div className={styles.metricsGrid}>
            <Card className={styles.metricCard}>
              <CardHeader
                header={
                  <Text className={styles.metricValue}>
                    {currentSnapshot.driftScore.toFixed(1)}
                  </Text>
                }
                description={
                  <Text className={styles.metricLabel}>Drift Score</Text>
                }
                action={
                  <Badge
                    className={styles.trendBadge}
                    appearance="tint"
                    color={
                      trendDirection === "improving"
                        ? "success"
                        : trendDirection === "degrading"
                          ? "danger"
                          : "informative"
                    }
                  >
                    {trendDirection === "improving"
                      ? "↓ Improving"
                      : trendDirection === "degrading"
                        ? "↑ Degrading"
                        : "→ Stable"}
                  </Badge>
                }
              />
            </Card>

            <Card className={styles.metricCard}>
              <CardHeader
                header={
                  <Text className={styles.metricValue}>
                    {currentSnapshot.totalViolations}
                  </Text>
                }
                description={
                  <Text className={styles.metricLabel}>Total Violations</Text>
                }
                action={
                  <Badge appearance="tint" color="warning">
                    {currentSnapshot.criticalViolations} Critical
                  </Badge>
                }
              />
            </Card>

            <Card className={styles.metricCard}>
              <CardHeader
                header={
                  <Text className={styles.metricValue}>
                    {violations.filter((v) => v.status === "resolved").length}
                  </Text>
                }
                description={
                  <Text className={styles.metricLabel}>
                    Resolved This Period
                  </Text>
                }
                action={
                  <Badge appearance="tint" color="success">
                    <CheckmarkCircle24Regular />
                  </Badge>
                }
              />
            </Card>

            <Card className={styles.metricCard}>
              <CardHeader
                header={
                  <Text className={styles.metricValue}>
                    {scoreChange >= 0
                      ? `+${scoreChange.toFixed(1)}`
                      : scoreChange.toFixed(1)}
                  </Text>
                }
                description={
                  <Text className={styles.metricLabel}>Score Change</Text>
                }
                action={
                  <Badge
                    appearance="filled"
                    color={scoreChange > 0 ? "danger" : "success"}
                  >
                    <Warning24Regular />
                  </Badge>
                }
              />
            </Card>
          </div>

          {/* Drift by Category */}
          <DriftTypeSummaryCards
            serviceGroupId={selectedServiceGroupId}
            accessToken={accessToken ?? undefined}
            onCategoryClick={handleDriftCategoryClick}
          />

          {/* Tabs */}
          <TabList
            selectedValue={selectedTab}
            onTabSelect={(_, data) => setSelectedTab(data.value as string)}
          >
            <Tab value="timeline">Timeline Chart</Tab>
            <Tab value="heatmap">Heatmap</Tab>
            <Tab value="violations">Violation List</Tab>
          </TabList>

          {/* Timeline Chart */}
          {selectedTab === "timeline" && (
            <Card className={styles.chartCard}>
              {" "}
              {snapshots.length === 0 ? (
                <div className={styles.emptyState}>
                  <Text size={300}>
                    No snapshot history for the selected period.
                  </Text>
                </div>
              ) : (
                <>
                  <Text size={500} weight="semibold">
                    Drift Score Trend
                  </Text>
                  <div className={styles.chartContainer}>
                    <ResponsiveContainer width="100%" height="100%">
                      <AreaChart data={chartData}>
                        <CartesianGrid strokeDasharray="3 3" />
                        <XAxis dataKey="date" />
                        <YAxis />
                        <Tooltip />
                        <Legend />
                        <Area
                          type="monotone"
                          dataKey="driftScore"
                          stroke={tokens.colorBrandBackground}
                          fill={tokens.colorBrandBackground2}
                          name="Drift Score"
                        />
                      </AreaChart>
                    </ResponsiveContainer>
                  </div>

                  <Text
                    size={500}
                    weight="semibold"
                    style={{ marginTop: tokens.spacingVerticalXL }}
                  >
                    Violations by Severity
                  </Text>
                  <div className={styles.chartContainer}>
                    <ResponsiveContainer width="100%" height="100%">
                      <LineChart data={chartData}>
                        <CartesianGrid strokeDasharray="3 3" />
                        <XAxis dataKey="date" />
                        <YAxis />
                        <Tooltip />
                        <Legend />
                        <Line
                          type="monotone"
                          dataKey="critical"
                          stroke={tokens.colorPaletteRedBorder1}
                          name="Critical"
                        />
                        <Line
                          type="monotone"
                          dataKey="high"
                          stroke={tokens.colorPaletteMarigoldBorder1}
                          name="High"
                        />
                        <Line
                          type="monotone"
                          dataKey="medium"
                          stroke={tokens.colorPaletteYellowBorder1}
                          name="Medium"
                        />
                        <Line
                          type="monotone"
                          dataKey="low"
                          stroke={tokens.colorPaletteLightGreenBorder1}
                          name="Low"
                        />
                      </LineChart>
                    </ResponsiveContainer>
                  </div>
                </>
              )}
            </Card>
          )}

          {/* Calendar Heatmap */}
          {selectedTab === "heatmap" && (
            <Card className={styles.heatmapCard}>
              <Text size={500} weight="semibold">
                Drift Violations Calendar
              </Text>
              <Text
                size={200}
                style={{
                  color: tokens.colorNeutralForeground3,
                  marginBottom: tokens.spacingVerticalM,
                }}
              >
                Each cell represents one day. Color intensity reflects peak
                violation count. Hover for details.
              </Text>
              {snapshots.length === 0 ? (
                <div className={styles.emptyState}>
                  <Text size={300}>
                    No snapshot history for the selected period.
                  </Text>
                </div>
              ) : (
                <DriftCalendarHeatmap
                  snapshots={snapshots}
                  timeRange={timeRange}
                />
              )}
            </Card>
          )}

          {/* Violation List */}
          {selectedTab === "violations" && (
            <div className={styles.violationList}>
              {driftCategoryFilter && (
                <div style={{ marginBottom: tokens.spacingVerticalS }}>
                  <Badge appearance="filled" color="brand">
                    Filtered: {driftCategoryFilter.replace(/Drift$/, "")}
                  </Badge>
                  <Button
                    appearance="transparent"
                    size="small"
                    onClick={() => setDriftCategoryFilter(null)}
                  >
                    Clear filter
                  </Button>
                </div>
              )}
              {violations
                .filter((v) => v.status === "active")
                .filter(
                  (v) =>
                    !driftCategoryFilter ||
                    v.driftCategory === driftCategoryFilter,
                ).length === 0 ? (
                <div className={styles.emptyState}>
                  <CheckmarkCircle24Regular
                    style={{
                      fontSize: "48px",
                      marginBottom: tokens.spacingVerticalM,
                    }}
                  />
                  <Text size={500} weight="semibold" block>
                    No active violations
                  </Text>
                  <Text
                    size={300}
                    block
                    style={{ marginTop: tokens.spacingVerticalS }}
                  >
                    All violations have been resolved for this service group.
                  </Text>
                </div>
              ) : (
                <DataGrid
                  items={violations
                    .filter((v) => v.status === "active")
                    .filter(
                      (v) =>
                        !driftCategoryFilter ||
                        v.driftCategory === driftCategoryFilter,
                    )}
                  columns={columns}
                  sortable
                  resizableColumns
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
                  <DataGridBody<Violation>>
                    {({ item, rowId }) => (
                      <DataGridRow<Violation> key={rowId}>
                        {({ renderCell }) => (
                          <DataGridCell>{renderCell(item)}</DataGridCell>
                        )}
                      </DataGridRow>
                    )}
                  </DataGridBody>
                </DataGrid>
              )}
            </div>
          )}
        </>
      )}
    </div>
  );
};

function deriveDriftRemediationGuidance(causeType?: string): string {
  switch ((causeType ?? "").toLowerCase()) {
    case "manualchange":
      return "Review the manual change, confirm ownership, and either approve the new state or revert it through a tracked remediation change set.";
    case "pipelinedeployment":
      return "Inspect the latest pipeline deployment, validate the generated infrastructure diff, and roll forward or roll back through the same delivery path.";
    case "policyeffect":
      return "Check the responsible policy assignment or deny effect, simulate the impact, and decide whether to remediate the resource or adjust the policy scope.";
    case "platformscaling":
      return "Inspect autoscale or platform-level configuration events, then confirm whether the drift reflects an expected scaling response or a tuning issue.";
    default:
      return "Review the evidence trail, inspect linked recommendations, and use change-set simulation before applying remediation.";
  }
}
