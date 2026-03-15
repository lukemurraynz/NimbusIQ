import { useEffect, useMemo, useState, useCallback, useRef } from "react";
import type { ReactNode, KeyboardEvent } from "react";
import { useNavigate, useSearchParams } from "react-router-dom";
import {
  Spinner,
  Text,
  Badge,
  Button,
  Checkbox,
  Input,
  Select,
  Tooltip,
  Card,
  CardHeader,
  makeStyles,
  tokens,
} from "@fluentui/react-components";
import {
  CheckmarkCircle20Regular,
  Warning20Regular,
  ErrorCircle20Regular,
  Info20Regular,
  ArrowSyncCircle20Regular,
  MoneyHand20Regular,
  Shield20Regular,
  HeartPulse20Regular,
  LeafOne20Regular,
  Building20Regular,
  Sparkle20Regular,
} from "@fluentui/react-icons";
import {
  controlPlaneApi,
  type Recommendation,
} from "../services/controlPlaneApi";
import { useAccessToken } from "../auth/useAccessToken";
import { useNotify } from "../components/useNotify";
import { AzurePageHeader } from "../components/AzurePageHeader";
import { AzureList } from "../components/AzureList";
import { useBlades } from "../components/useBlades";
import { RecommendationDecisionPanel } from "../components/RecommendationDecisionPanel";
import { useRecommendationSummary } from "../hooks/useRecommendationSummary";
import {
  RECOMMENDATION_WORKFLOW_STATUS,
  isQueueCandidateStatus,
  normalizeRecommendationStatus,
} from "../constants/recommendationWorkflowStatus";

// Re-use the full Recommendation shape from the API so title/description/rationale
// are not stripped before they reach the blade.
type RecommendationSummary = Recommendation & { confidence?: number };

const useStyles = makeStyles({
  container: {
    display: "flex",
    flexDirection: "column",
    height: "100%",
  },
  filterBar: {
    display: "flex",
    gap: tokens.spacingHorizontalS,
    padding: tokens.spacingHorizontalL,
    backgroundColor: tokens.colorNeutralBackground1,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    flexWrap: "wrap",
    alignItems: "center",
  },
  filterToggle: {
    marginLeft: "auto",
  },
  filterPanel: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
    gap: tokens.spacingHorizontalM,
    padding: tokens.spacingHorizontalL,
    backgroundColor: tokens.colorNeutralBackground2,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  filterBlock: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalXS,
    minWidth: 0,
  },
  filterLabel: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  filterActions: {
    display: "flex",
    alignItems: "flex-end",
  },
  scoreExplainerCard: {
    margin: tokens.spacingHorizontalL,
    padding: tokens.spacingHorizontalM,
  },
  scoreExplainerGrid: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
    gap: tokens.spacingHorizontalM,
  },
  scoreExplainerTitle: {
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightSemibold,
  },
  scoreExplainerBody: {
    color: tokens.colorNeutralForeground2,
    fontSize: tokens.fontSizeBase200,
  },
  scoreExplainerMeta: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase200,
  },
  detailsLink: {
    color: tokens.colorBrandForeground1,
    fontSize: tokens.fontSizeBase300,
  },
  badgeRow: {
    display: "flex",
    gap: tokens.spacingHorizontalS,
    flexWrap: "wrap",
  },
  subtle: {
    color: tokens.colorNeutralForeground3,
  },
  bladeLayout: {
    display: "grid",
    gridTemplateColumns: "minmax(0, 1.45fr) minmax(360px, 0.95fr)",
    gap: tokens.spacingHorizontalXL,
    alignItems: "start",
    width: "100%",
    "@media (max-width: 1280px)": {
      gridTemplateColumns: "1fr",
    },
  },
  bladeMain: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalM,
    minWidth: 0,
  },
  bladeSide: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalM,
    minWidth: 0,
    position: "sticky",
    top: tokens.spacingVerticalM,
    alignSelf: "start",
    "@media (max-width: 1280px)": {
      position: "static",
    },
  },
  bladeLeadCard: {
    padding: tokens.spacingHorizontalL,
  },
  bladeContextGrid: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(220px, 1fr))",
    gap: tokens.spacingHorizontalM,
  },
  bladeMetricGrid: {
    display: "grid",
    gridTemplateColumns: "repeat(2, minmax(0, 1fr))",
    gap: tokens.spacingHorizontalS,
  },
  bladeMetricCard: {
    padding: tokens.spacingHorizontalM,
  },
  bladeSectionCard: {
    padding: tokens.spacingHorizontalL,
  },
  bladeMetaList: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalS,
  },
  bladeMetaRow: {
    display: "flex",
    justifyContent: "space-between",
    gap: tokens.spacingHorizontalM,
    alignItems: "flex-start",
  },
  bladeResourceText: {
    color: tokens.colorNeutralForeground3,
  },
  summaryCard: {
    margin: tokens.spacingHorizontalL,
    padding: tokens.spacingHorizontalM,
  },
  summaryHeaderActions: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalS,
  },
  summaryMetaRow: {
    display: "flex",
    flexWrap: "wrap",
    gap: tokens.spacingHorizontalS,
    marginTop: tokens.spacingVerticalXS,
    marginBottom: tokens.spacingVerticalS,
  },
  summaryText: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalXS,
    lineHeight: "1.45",
    fontSize: tokens.fontSizeBase200,
  },
  summaryViewport: {
    overflowY: "auto",
    overflowX: "hidden",
    paddingRight: tokens.spacingHorizontalXS,
    scrollbarGutter: "stable",
  },
  summaryViewportCollapsed: {
    maxHeight: "180px",
  },
  summaryViewportExpanded: {
    maxHeight: "440px",
  },
  summaryHeading: {
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightSemibold,
  },
  summarySubHeading: {
    fontSize: tokens.fontSizeBase300,
    fontWeight: tokens.fontWeightSemibold,
    marginTop: tokens.spacingVerticalXS,
  },
  summaryList: {
    margin: 0,
    paddingLeft: tokens.spacingHorizontalL,
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalXXS,
  },
  summaryParagraph: {
    color: tokens.colorNeutralForeground2,
  },
  spotlightGrid: {
    margin: `0 ${tokens.spacingHorizontalL}`,
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(320px, 1fr))",
    gap: tokens.spacingHorizontalM,
  },
  spotlightCard: {
    padding: tokens.spacingHorizontalM,
  },
  spotlightHeader: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "center",
    gap: tokens.spacingHorizontalS,
    marginBottom: tokens.spacingVerticalS,
  },
  inboxColumns: {
    display: "grid",
    gridTemplateColumns: "repeat(3, minmax(0, 1fr))",
    gap: tokens.spacingHorizontalS,
    "@media (max-width: 1100px)": {
      gridTemplateColumns: "1fr",
    },
  },
  inboxColumn: {
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: tokens.spacingHorizontalS,
  },
  inboxList: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalXS,
  },
  inboxItem: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "center",
    gap: tokens.spacingHorizontalS,
  },
  insightRow: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "baseline",
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalXS} 0`,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
});

function statusIcon(status: string | undefined) {
  switch ((status ?? "").toLowerCase()) {
    case "approved":
      return <CheckmarkCircle20Regular />;
    case "rejected":
      return <ErrorCircle20Regular />;
    case "pending":
      return <Warning20Regular />;
    default:
      return <Info20Regular />;
  }
}

function badgeColor(
  priority: string | undefined,
): "danger" | "warning" | "success" | "brand" {
  switch ((priority ?? "").toLowerCase()) {
    case "critical":
      return "danger";
    case "high":
      return "warning";
    case "medium":
      return "brand";
    default:
      return "success";
  }
}

function priorityHint(priority: string | undefined): string {
  switch ((priority ?? "").toLowerCase()) {
    case "critical":
      return "Immediate action recommended. High potential blast radius if deferred.";
    case "high":
      return "Address soon. Elevated risk or cost impact is likely.";
    case "medium":
      return "Plan in current cycle. Material but non-urgent impact.";
    case "low":
      return "Backlog candidate. Low short-term impact.";
    default:
      return "Priority level not classified.";
  }
}

function truncateCorrelationId(correlationId: string | undefined): string {
  if (!correlationId) return "";
  return correlationId.length > 8
    ? `${correlationId.slice(0, 8)}...`
    : correlationId;
}

function extractResourceName(armId: string): string {
  const segments = armId.split("/");
  return segments.length > 1 ? segments[segments.length - 1] : armId;
}

function extractResourceType(armId: string): string {
  const providerIdx = armId.toLowerCase().indexOf("/providers/");
  if (providerIdx === -1) return "";
  const afterProvider = armId.slice(providerIdx + "/providers/".length);
  const parts = afterProvider.split("/");
  return parts.length >= 2 ? `${parts[0]}/${parts[1]}` : afterProvider;
}

function parseMonthlySavings(estimatedImpact: string | undefined): number {
  if (!estimatedImpact) return 0;
  try {
    const parsed = JSON.parse(estimatedImpact) as {
      monthlySavings?: number;
      costDelta?: number;
    };
    if (typeof parsed.monthlySavings === "number") {
      return Math.max(0, parsed.monthlySavings);
    }
    if (typeof parsed.costDelta === "number") {
      return Math.max(0, -parsed.costDelta);
    }
  } catch {
    return 0;
  }
  return 0;
}

function categoryIcon(category: string | undefined) {
  switch (category?.toLowerCase()) {
    case "finops":
      return <MoneyHand20Regular />;
    case "reliability":
      return <HeartPulse20Regular />;
    case "sustainability":
      return <LeafOne20Regular />;
    case "architecture":
      return <Building20Regular />;
    default:
      return <Shield20Regular />;
  }
}

function normalizeSourceLabel(item: RecommendationSummary): string {
  if (item.sourceLabel) return item.sourceLabel;
  if (item.source) {
    return item.source
      .split("_")
      .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
      .join(" ");
  }

  if (item.confidenceSource === "ai_foundry") return "AI Synthesis";
  return "Unclassified";
}

function toDisplayText(value: unknown, fallback = "Not available"): string {
  if (value == null) return fallback;
  if (typeof value === "string") {
    const trimmed = value.trim();
    if (!trimmed) return fallback;

    try {
      const parsed = JSON.parse(trimmed) as unknown;
      if (Array.isArray(parsed)) {
        return parsed.map((entry) => String(entry)).join(" • ");
      }
      if (parsed && typeof parsed === "object") {
        return JSON.stringify(parsed);
      }
    } catch {
      // Keep original string when it is not JSON.
    }

    return trimmed;
  }

  if (Array.isArray(value)) {
    return value.map((entry) => String(entry)).join(" • ");
  }

  if (typeof value === "object") {
    return JSON.stringify(value);
  }

  return String(value);
}

interface ViolationContext {
  currentFieldLabel: string;
  currentValues: string[];
  requiredValue: string;
}

function parseViolationContext(
  changeContext: string | undefined,
): ViolationContext | null {
  if (!changeContext) return null;
  try {
    const ctx = JSON.parse(changeContext) as Record<string, unknown>;
    const currentFieldLabel =
      typeof ctx.currentFieldLabel === "string" ? ctx.currentFieldLabel : "value";
    const currentValues = Array.isArray(ctx.currentValues)
      ? (ctx.currentValues as unknown[]).map(String).filter(Boolean)
      : [];
    const requiredValue =
      typeof ctx.requiredValue === "string" ? ctx.requiredValue : "";
    if (!currentValues.length && !requiredValue) return null;
    return { currentFieldLabel, currentValues, requiredValue };
  } catch {
    return null;
  }
}

function renderInlineMarkdown(text: string) {
  const parts: Array<{ text: string; bold: boolean }> = [];
  const pattern = /\*\*(.*?)\*\*/g;
  let last = 0;
  let match: RegExpExecArray | null;

  while ((match = pattern.exec(text)) !== null) {
    if (match.index > last) {
      parts.push({ text: text.slice(last, match.index), bold: false });
    }
    parts.push({ text: match[1], bold: true });
    last = pattern.lastIndex;
  }

  if (last < text.length) {
    parts.push({ text: text.slice(last), bold: false });
  }

  return parts.map((part, idx) =>
    part.bold ? (
      <strong key={idx}>{part.text}</strong>
    ) : (
      <span key={idx}>{part.text}</span>
    ),
  );
}

const defaultCategories = [
  "architecture",
  "finops",
  "reliability",
  "sustainability",
];

function formatCategory(category: string): string {
  const normalized = category.trim().toLowerCase();
  if (normalized === "finops") return "FinOps";
  return normalized
    .split("_")
    .map((part) => part.charAt(0).toUpperCase() + part.slice(1))
    .join(" ");
}

/**
 * Recommendations page with Azure Portal-style list and blade navigation.
 * Opens recommendation details in a blade for master-detail experience.
 */
export function RecommendationsPage() {
  const styles = useStyles();
  const { accessToken } = useAccessToken();
  const { openBlade, closeAllBlades } = useBlades();
  const navigate = useNavigate();
  const [searchParams, setSearchParams] = useSearchParams();
  const notify = useNotify();
  const summary = useRecommendationSummary();

  useEffect(() => {
    document.title = "NimbusIQ — Recommendations";
  }, []);

  // Status filter moved from URL params to local state for API safety
  const [status, setStatus] = useState<string | undefined>(undefined);
  const [analysisRunFilter, setAnalysisRunFilter] = useState<
    string | undefined
  >(searchParams.get("analysisRunId") ?? undefined);
  const [serviceGroupFilter, setServiceGroupFilter] = useState<
    string | undefined
  >(searchParams.get("serviceGroupId") ?? undefined);

  const [items, setItems] = useState<RecommendationSummary[]>([]);
  const [selectedIds, setSelectedIds] = useState<Set<string>>(new Set());
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | undefined>(undefined);
  const [sourceFilter, setSourceFilter] = useState<string>("");
  const [trustFilter, setTrustFilter] = useState<string>("");
  const [categoryFilter, setCategoryFilter] = useState<string>("");
  const [confidenceBandFilter, setConfidenceBandFilter] = useState<string>("");
  const [queueBandFilter, setQueueBandFilter] = useState<string>("");
  const [freshnessBandFilter, setFreshnessBandFilter] = useState<string>("");
  const [searchQuery, setSearchQuery] = useState<string>("");
  const [filtersOpen, setFiltersOpen] = useState(true);
  const [orderBy, setOrderBy] = useState<string>("risk-weighted");
  const [bulkBusy, setBulkBusy] = useState(false);
  const [offset, setOffset] = useState(0);
  const [hasMore, setHasMore] = useState(true);
  const [pageLoading, setPageLoading] = useState(false);
  const [refreshToken, setRefreshToken] = useState(0);
  const [summaryExpanded, setSummaryExpanded] = useState(false);
  const summaryGenerationTokenRef = useRef<number | null>(null);
  const pageSize = 200;

  const summaryBlocks = useMemo(() => {
    const text = summary.text.trim();
    if (!text) return null;

    const lines = text.split(/\r?\n/);
    const blocks: ReactNode[] = [];

    for (let i = 0; i < lines.length; i++) {
      const line = lines[i].trim();
      if (!line) continue;

      if (line.startsWith("## ") || line.startsWith("# ")) {
        blocks.push(
          <div key={`h2-${i}`} className={styles.summaryHeading}>
            {renderInlineMarkdown(
              line.startsWith("## ") ? line.slice(3) : line.slice(2),
            )}
          </div>,
        );
        continue;
      }

      if (line.startsWith("### ") || line.endsWith(":")) {
        blocks.push(
          <div key={`h3-${i}`} className={styles.summarySubHeading}>
            {renderInlineMarkdown(
              line.startsWith("### ") ? line.slice(4) : line.replace(/:$/, ""),
            )}
          </div>,
        );
        continue;
      }

      if (line.startsWith("- ")) {
        const items: string[] = [];
        let j = i;
        while (j < lines.length && lines[j].trim().startsWith("- ")) {
          items.push(lines[j].trim().slice(2));
          j++;
        }
        blocks.push(
          <ul key={`ul-${i}`} className={styles.summaryList}>
            {items.map((item, idx) => (
              <li key={`${i}-${idx}`}>{renderInlineMarkdown(item)}</li>
            ))}
          </ul>,
        );
        i = j - 1;
        continue;
      }

      blocks.push(
        <div key={`p-${i}`} className={styles.summaryParagraph}>
          {renderInlineMarkdown(line)}
        </div>,
      );
    }

    return blocks;
  }, [
    summary.text,
    styles.summaryHeading,
    styles.summaryList,
    styles.summaryParagraph,
    styles.summarySubHeading,
  ]);

  useEffect(() => {
    setAnalysisRunFilter(searchParams.get("analysisRunId") ?? undefined);
    setServiceGroupFilter(searchParams.get("serviceGroupId") ?? undefined);
  }, [searchParams]);

  const baseFilters = useMemo(
    () => ({
      status,
      analysisRunId: analysisRunFilter,
      serviceGroupId: serviceGroupFilter,
      orderBy,
      source: sourceFilter,
      trustLevel: trustFilter,
      category: categoryFilter,
      confidenceBand: confidenceBandFilter,
      queueBand: queueBandFilter,
      freshnessBand: freshnessBandFilter,
      search: searchQuery.trim() || undefined,
    }),
    [
      analysisRunFilter,
      categoryFilter,
      confidenceBandFilter,
      freshnessBandFilter,
      orderBy,
      queueBandFilter,
      searchQuery,
      serviceGroupFilter,
      sourceFilter,
      status,
      trustFilter,
    ],
  );

  const filterSignature = useMemo(
    () => JSON.stringify(baseFilters),
    [baseFilters],
  );

  const requestFilters = useMemo(
    () => ({
      ...baseFilters,
      limit: pageSize,
      offset,
    }),
    [baseFilters, offset, pageSize],
  );

  useEffect(() => {
    setOffset(0);
    setHasMore(true);
    setItems([]);
    setPageLoading(false);
  }, [filterSignature]);

  const categoryOptions = useMemo(() => {
    const categories = new Set<string>(defaultCategories);
    for (const item of items) {
      if (item.category) {
        categories.add(item.category.toLowerCase());
      }
    }
    return Array.from(categories).sort((a, b) => a.localeCompare(b));
  }, [items]);

  const displayedItems = useMemo(() => items, [items]);

  useEffect(() => {
    let cancelled = false;

    async function run() {
      const isInitial = offset === 0;
      if (isInitial) {
        setLoading(true);
      } else {
        setPageLoading(true);
      }
      setError(undefined);
      try {
        const result = await controlPlaneApi.listRecommendations(
          requestFilters,
          accessToken,
        );
        if (cancelled) return;
        const page = result.value ?? [];
        setHasMore(page.length === pageSize);
        setItems((prev) => {
          const deduped = new Map<string, RecommendationSummary>();
          const seed = isInitial ? [] : prev;
          for (const existing of seed) {
            deduped.set(existing.id, existing);
          }
          for (const raw of page) {
            const recommendation = raw as RecommendationSummary & {
              confidence?: number;
            };
            const confidenceScore =
              typeof recommendation.confidenceScore === "number" &&
              Number.isFinite(recommendation.confidenceScore)
                ? recommendation.confidenceScore
                : typeof recommendation.confidence === "number" &&
                    Number.isFinite(recommendation.confidence)
                  ? recommendation.confidence
                  : 0;

            deduped.set(recommendation.id, {
              ...recommendation,
              priority: recommendation.priority ?? "medium",
              confidenceScore,
            });
          }

          return Array.from(deduped.values());
        });
      } catch (e) {
        if (cancelled) return;
        setError(e instanceof Error ? e.message : String(e));
      } finally {
        if (!cancelled) {
          if (offset === 0) {
            setLoading(false);
          } else {
            setPageLoading(false);
          }
        }
      }
    }

    void run();

    return () => {
      cancelled = true;
    };
  }, [accessToken, offset, pageSize, refreshToken, requestFilters]);

  const priorityInbox = useMemo(() => {
    const pending = displayedItems
      .filter((item) => isQueueCandidateStatus(item.status))
      .slice()
      .sort(
        (a, b) =>
          Number(b.riskWeightedScore ?? 0) - Number(a.riskWeightedScore ?? 0),
      );

    const doNow = pending.filter((item) => {
      const priority = item.priority.toLowerCase();
      const queueScore = Number(item.riskWeightedScore ?? 0);
      return (
        priority === "critical" || priority === "high" || queueScore >= 0.75
      );
    });
    const thisWeek = pending.filter((item) => {
      if (doNow.some((x) => x.id === item.id)) return false;
      const priority = item.priority.toLowerCase();
      const queueScore = Number(item.riskWeightedScore ?? 0);
      return priority === "medium" || queueScore >= 0.55;
    });
    const backlog = pending.filter(
      (item) =>
        !doNow.some((x) => x.id === item.id) &&
        !thisWeek.some((x) => x.id === item.id),
    );

    return {
      doNow: doNow.slice(0, 5),
      thisWeek: thisWeek.slice(0, 5),
      backlog: backlog.slice(0, 5),
    };
  }, [displayedItems]);

  const businessImpact = useMemo(() => {
    const pending = displayedItems.filter((item) =>
      isQueueCandidateStatus(item.status),
    );
    const outageRisk = pending.filter(
      (item) =>
        item.category?.toLowerCase() === "reliability" &&
        ["critical", "high"].includes(item.priority.toLowerCase()),
    ).length;
    const complianceExposure = pending.filter((item) => {
      const source =
        `${item.source ?? ""} ${item.triggerReason ?? ""}`.toLowerCase();
      return (
        source.includes("policy") ||
        source.includes("defender") ||
        source.includes("psrule")
      );
    }).length;
    const monthlyCostOpportunity = pending
      .filter((item) => item.category?.toLowerCase() === "finops")
      .reduce(
        (sum, item) => sum + parseMonthlySavings(item.estimatedImpact),
        0,
      );
    const sustainabilityOpportunity = pending.filter(
      (item) => item.category?.toLowerCase() === "sustainability",
    ).length;

    return {
      outageRisk,
      complianceExposure,
      monthlyCostOpportunity,
      sustainabilityOpportunity,
    };
  }, [displayedItems]);

  const runDelta = useMemo(() => {
    const byRun = new Map<
      string,
      { createdAt: string; recommendationIds: Set<string> }
    >();
    for (const item of displayedItems) {
      if (!item.analysisRunId) continue;
      const bucket = byRun.get(item.analysisRunId) ?? {
        createdAt: item.createdAt,
        recommendationIds: new Set<string>(),
      };
      if (
        new Date(item.createdAt).getTime() >
        new Date(bucket.createdAt).getTime()
      ) {
        bucket.createdAt = item.createdAt;
      }
      bucket.recommendationIds.add(item.id);
      byRun.set(item.analysisRunId, bucket);
    }
    const runs = Array.from(byRun.entries())
      .map(([runId, data]) => ({ runId, ...data }))
      .sort(
        (a, b) =>
          new Date(b.createdAt).getTime() - new Date(a.createdAt).getTime(),
      );
    if (runs.length < 2) {
      return { newItems: 0, resolvedItems: 0 };
    }
    const current = runs[0].recommendationIds;
    const previous = runs[1].recommendationIds;
    let newItems = 0;
    let resolvedItems = 0;
    for (const id of current) if (!previous.has(id)) newItems++;
    for (const id of previous) if (!current.has(id)) resolvedItems++;
    return { newItems, resolvedItems };
  }, [displayedItems]);

  const summaryInsights = useMemo(() => {
    const pending = displayedItems.filter((item) =>
      isQueueCandidateStatus(item.status),
    ).length;
    const highPriority = displayedItems.filter((item) => {
      const normalizedStatus = normalizeRecommendationStatus(item.status);
      const priority = item.priority.toLowerCase();
      return (
        isQueueCandidateStatus(normalizedStatus) &&
        (priority === "critical" || priority === "high")
      );
    }).length;

    return {
      total: displayedItems.length,
      pending,
      highPriority,
      categories: categoryOptions.length,
    };
  }, [categoryOptions.length, displayedItems]);

  const selectedCount = selectedIds.size;
  const pendingStatusFilter = `${RECOMMENDATION_WORKFLOW_STATUS.pending},${RECOMMENDATION_WORKFLOW_STATUS.pendingApproval},${RECOMMENDATION_WORKFLOW_STATUS.manualReview}`;

  useEffect(() => {
    if (loading || pageLoading || error || displayedItems.length === 0) {
      return;
    }

    if (summary.streaming) {
      return;
    }

    if (
      summaryGenerationTokenRef.current === refreshToken &&
      summary.text.trim().length > 0
    ) {
      return;
    }

    summaryGenerationTokenRef.current = refreshToken;
    void summary.generate({ accessToken });
  }, [
    accessToken,
    displayedItems.length,
    error,
    loading,
    pageLoading,
    refreshToken,
    summary.generate,
    summary.streaming,
    summary.text,
  ]);

  const handleToggleSelection = useCallback((id: string) => {
    setSelectedIds((prev) => {
      const next = new Set(prev);
      if (next.has(id)) {
        next.delete(id);
      } else {
        next.add(id);
      }

      return next;
    });
  }, []);

  const handleBulkDecision = useCallback(
    async (decision: "approve" | "reject") => {
      if (selectedIds.size === 0) {
        return;
      }

      setBulkBusy(true);
      const ids = Array.from(selectedIds);
      let succeeded = 0;

      for (const recommendationId of ids) {
        try {
          if (decision === "approve") {
            await controlPlaneApi.approveRecommendation(
              recommendationId,
              "Bulk approved from recommendations list",
              undefined,
              accessToken,
            );
          } else {
            await controlPlaneApi.rejectRecommendation(
              recommendationId,
              "Bulk rejected from recommendations list",
              accessToken,
            );
          }
          succeeded++;
        } catch {
          // Continue processing remaining selections.
        }
      }

      setSelectedIds(new Set());
      setItems((prev) =>
        prev.map((item) => {
          if (!ids.includes(item.id)) {
            return item;
          }

          return {
            ...item,
            status:
              decision === "approve"
                ? RECOMMENDATION_WORKFLOW_STATUS.approved
                : RECOMMENDATION_WORKFLOW_STATUS.rejected,
          };
        }),
      );

      notify({
        title:
          decision === "approve"
            ? "Bulk approve complete"
            : "Bulk reject complete",
        body: `${succeeded} of ${ids.length} recommendation(s) updated.`,
        intent: succeeded === ids.length ? "success" : "warning",
      });
      setBulkBusy(false);
    },
    [accessToken, notify, selectedIds],
  );

  const handleRefresh = () => {
    setItems([]);
    setOffset(0);
    setHasMore(true);
    setError(undefined);
    setPageLoading(false);
    setRefreshToken((prev) => prev + 1);
  };

  const refreshItem = useCallback(
    (updatedId: string) => {
      void controlPlaneApi
        .getRecommendation(updatedId, accessToken)
        .then((updated) => {
          setItems((prev) =>
            prev.map((it) =>
              it.id === updatedId ? { ...it, ...updated } : it,
            ),
          );
        })
        .catch((e: unknown) => {
          notify({
            title: "Failed to refresh recommendation",
            body: e instanceof Error ? e.message : String(e),
            intent: "error",
          });
        });
    },
    [accessToken, notify],
  );

  const handleStatusKeyDown = useCallback(
    (event: KeyboardEvent<HTMLElement>, nextStatus: string | undefined) => {
      if (event.key === "Enter" || event.key === " ") {
        event.preventDefault();
        setStatus(nextStatus);
      }
    },
    [],
  );

  const statusBadgeProps = useCallback(
    (nextStatus: string | undefined) => ({
      role: "button" as const,
      tabIndex: 0,
      "aria-pressed": status === nextStatus,
      onClick: () => setStatus(nextStatus),
      onKeyDown: (event: KeyboardEvent<HTMLElement>) =>
        handleStatusKeyDown(event, nextStatus),
      style: { cursor: "pointer" },
    }),
    [handleStatusKeyDown, status],
  );

  const handleItemClick = (item: RecommendationSummary) => {
    openBlade({
      id: `recommendation-${item.id}`,
      title: item.title ?? item.recommendationType,
      size: "large",
      content: (
        <div className={styles.bladeLayout}>
          <div className={styles.bladeMain}>
            <Card className={styles.bladeLeadCard}>
              {item.title && (
                <Text size={700} weight="semibold" block>
                  {item.title}
                </Text>
              )}
              {item.description && (
                <Text
                  size={400}
                  block
                  style={{ marginTop: tokens.spacingVerticalS }}
                >
                  {item.description}
                </Text>
              )}
              <div
                className={styles.badgeRow}
                style={{ marginTop: tokens.spacingVerticalM }}
              >
                <Badge appearance="outline">{normalizeSourceLabel(item)}</Badge>
                {item.wellArchitectedPillar && (
                  <Badge appearance="outline">
                    {item.wellArchitectedPillar}
                  </Badge>
                )}
                <Badge appearance="tint" color={badgeColor(item.priority)}>
                  {String(item.priority).toUpperCase()}
                </Badge>
              </div>
            </Card>

            <Card className={styles.bladeSectionCard}>
              <Text size={500} weight="semibold" block>
                Recommendation context
              </Text>
              <div
                className={styles.bladeContextGrid}
                style={{ marginTop: tokens.spacingVerticalM }}
              >
                <div>
                  <Text size={200} className={styles.subtle} block>
                    Why this exists
                  </Text>
                  <Text size={300} block>
                    {toDisplayText(
                      item.rationale ?? item.triggerReason,
                      "No rationale provided.",
                    )}
                  </Text>
                </div>
                <div>
                  <Text size={200} className={styles.subtle} block>
                    Resource
                  </Text>
                  <Tooltip content={item.resourceId} relationship="description">
                    <Text size={300} block>
                      {extractResourceName(item.resourceId)}
                    </Text>
                  </Tooltip>
                  <Text size={200} block className={styles.bladeResourceText}>
                    {extractResourceType(item.resourceId)}
                  </Text>
                </div>
                <div>
                  <Text size={200} className={styles.subtle} block>
                    Action
                  </Text>
                  <Text size={300} block>
                    {item.actionType}
                  </Text>
                </div>
                <div>
                  <Text size={200} className={styles.subtle} block>
                    Status
                  </Text>
                  <Text size={300} block>
                    {item.status}
                  </Text>
                </div>
                {(() => {
                  const ctx = parseViolationContext(item.changeContext);
                  if (!ctx) return null;
                  return (
                    <>
                      {ctx.currentValues.length > 0 && (
                        <div>
                          <Text size={200} className={styles.subtle} block>
                            Current {ctx.currentFieldLabel}
                          </Text>
                          <Text size={300} block>
                            {ctx.currentValues.join(", ")}
                          </Text>
                        </div>
                      )}
                      {ctx.requiredValue && (
                        <div>
                          <Text size={200} className={styles.subtle} block>
                            Required
                          </Text>
                          <Text size={300} block>
                            {ctx.requiredValue}
                          </Text>
                        </div>
                      )}
                    </>
                  );
                })()}
              </div>
            </Card>

            {item.rationale && (
              <Card className={styles.bladeSectionCard}>
                <Text size={500} weight="semibold" block>
                  Rationale
                </Text>
                <Text
                  size={300}
                  block
                  style={{
                    color: tokens.colorNeutralForeground3,
                    marginTop: tokens.spacingVerticalS,
                  }}
                >
                  {item.rationale}
                </Text>
              </Card>
            )}

            {item.evidenceReferences && (
              <Card className={styles.bladeSectionCard}>
                <Text size={500} weight="semibold" block>
                  Key evidence
                </Text>
                <Text
                  size={300}
                  block
                  style={{ marginTop: tokens.spacingVerticalS }}
                >
                  {toDisplayText(item.evidenceReferences)}
                </Text>
              </Card>
            )}
          </div>

          <div className={styles.bladeSide}>
            <Card className={styles.bladeSectionCard}>
              <Text size={500} weight="semibold" block>
                Decision metrics
              </Text>
              <div
                className={styles.bladeMetricGrid}
                style={{ marginTop: tokens.spacingVerticalM }}
              >
                <Card className={styles.bladeMetricCard}>
                  <Text size={200} className={styles.subtle} block>
                    Confidence
                  </Text>
                  <Text size={600} weight="semibold" block>
                    {(item.confidenceScore * 100).toFixed(0)}%
                  </Text>
                </Card>
                <Card className={styles.bladeMetricCard}>
                  <Text size={200} className={styles.subtle} block>
                    Trust
                  </Text>
                  <Text size={600} weight="semibold" block>
                    {Math.round(
                      Number(item.trustScore ?? item.confidenceScore) * 100,
                    )}
                    %
                  </Text>
                </Card>
                <Card className={styles.bladeMetricCard}>
                  <Text size={200} className={styles.subtle} block>
                    Risk
                  </Text>
                  <Text size={600} weight="semibold" block>
                    {Math.round(Number(item.riskScore ?? 0) * 100)}%
                  </Text>
                </Card>
                <Card className={styles.bladeMetricCard}>
                  <Text size={200} className={styles.subtle} block>
                    Queue score
                  </Text>
                  <Text size={600} weight="semibold" block>
                    {Math.round(Number(item.riskWeightedScore ?? 0) * 100)}%
                  </Text>
                </Card>
              </div>
              <Text
                size={200}
                className={styles.subtle}
                style={{ marginTop: tokens.spacingVerticalS }}
              >
                Trust blends confidence with evidence completeness and data
                freshness. Queue score is the risk-weighted signal used to
                prioritize the inbox.
              </Text>
            </Card>

            <Card className={styles.bladeSectionCard}>
              <Text size={500} weight="semibold" block>
                Governance metadata
              </Text>
              <div
                className={styles.bladeMetaList}
                style={{ marginTop: tokens.spacingVerticalM }}
              >
                <div className={styles.bladeMetaRow}>
                  <Text size={200} className={styles.subtle}>
                    Priority hint
                  </Text>
                  <Text size={200}>{priorityHint(item.priority)}</Text>
                </div>
                <div className={styles.bladeMetaRow}>
                  <Text size={200} className={styles.subtle}>
                    Approvals
                  </Text>
                  <Text size={300}>
                    {item.receivedApprovals}/{item.requiredApprovals}
                  </Text>
                </div>
                {item.correlationId && (
                  <div className={styles.bladeMetaRow}>
                    <Text size={200} className={styles.subtle}>
                      Correlation
                    </Text>
                    <Text size={200} style={{ fontFamily: "monospace" }}>
                      {truncateCorrelationId(item.correlationId)}
                    </Text>
                  </div>
                )}
              </div>
            </Card>

            <RecommendationDecisionPanel
              recommendationId={item.id}
              status={item.status}
              onChanged={() => refreshItem(item.id)}
            />

            <Button
              appearance="secondary"
              className={styles.detailsLink}
              onClick={() => {
                closeAllBlades();
                navigate(`/recommendations/${item.id}`);
              }}
            >
              View full details &rarr;
            </Button>
          </div>
        </div>
      ),
    });
  };

  const columns = [
    {
      key: "select",
      header: "",
      width: "4%",
      render: (item: RecommendationSummary) => (
        <div onClick={(event) => event.stopPropagation()}>
          <Checkbox
            checked={selectedIds.has(item.id)}
            onChange={() => handleToggleSelection(item.id)}
            aria-label={`Select recommendation ${item.id}`}
          />
        </div>
      ),
    },
    {
      key: "type",
      header: "Recommendation",
      width: "30%",
      render: (item: RecommendationSummary) => (
        <div
          style={{
            display: "flex",
            alignItems: "flex-start",
            gap: tokens.spacingHorizontalS,
          }}
        >
          <span
            style={{ marginTop: "2px", color: tokens.colorNeutralForeground3 }}
          >
            {categoryIcon(item.category)}
          </span>
          <div>
            <Text weight="semibold">
              {item.title ?? item.recommendationType}
            </Text>
            <Text
              size={200}
              block
              style={{
                color: tokens.colorNeutralForeground3,
                marginTop: tokens.spacingVerticalXXS,
              }}
            >
              {item.category ?? item.recommendationType} · {item.actionType}
            </Text>
            {Number(item.freshnessDays ?? 0) >= 30 && (
              <Badge
                appearance="outline"
                color="warning"
                size="small"
                style={{ marginTop: tokens.spacingVerticalXXS }}
              >
                Stale {item.freshnessDays}d
              </Badge>
            )}
          </div>
        </div>
      ),
    },
    {
      key: "resource",
      header: "Resource",
      width: "20%",
      render: (item: RecommendationSummary) => (
        <Tooltip content={item.resourceId} relationship="description">
          <div>
            <Text weight="semibold">
              {extractResourceName(item.resourceId)}
            </Text>
            <Text
              size={200}
              block
              style={{
                color: tokens.colorNeutralForeground3,
                marginTop: tokens.spacingVerticalXXS,
              }}
            >
              {extractResourceType(item.resourceId)}
            </Text>
          </div>
        </Tooltip>
      ),
    },
    {
      key: "serviceGroup",
      header: "Service Group",
      width: "10%",
      render: (item: RecommendationSummary) => (
        <Text size={300}>{item.serviceGroupName ?? "—"}</Text>
      ),
    },
    {
      key: "status",
      header: "Status",
      width: "15%",
      render: (item: RecommendationSummary) => (
        <div style={{ display: "flex", alignItems: "center" }}>
          {statusIcon(item.status)}
          <Text>{normalizeRecommendationStatus(item.status)}</Text>
        </div>
      ),
    },
    {
      key: "priority",
      header: "Priority",
      width: "15%",
      render: (item: RecommendationSummary) => (
        <Tooltip
          content={priorityHint(item.priority)}
          relationship="description"
        >
          <Badge appearance="filled" color={badgeColor(item.priority)}>
            {item.priority}
          </Badge>
        </Tooltip>
      ),
    },
    {
      key: "risk",
      header: "Risk",
      width: "10%",
      render: (item: RecommendationSummary) => {
        const risk = Number(item.riskScore ?? 0);
        const riskLabel = `${Math.round(risk * 100)}%`;
        const color =
          risk >= 0.8 ? "danger" : risk >= 0.6 ? "warning" : "brand";
        return (
          <Badge appearance="tint" color={color}>
            {riskLabel}
          </Badge>
        );
      },
    },
    {
      key: "queueScore",
      header: "Queue",
      width: "10%",
      render: (item: RecommendationSummary) => {
        const score = Number(item.riskWeightedScore ?? 0);
        const color =
          score >= 0.8 ? "danger" : score >= 0.6 ? "warning" : "brand";
        return (
          <Tooltip
            content="Queue score is the risk-weighted signal used to prioritize recommendations."
            relationship="description"
          >
            <Badge appearance="filled" color={color}>
              {Math.round(score * 100)}%
            </Badge>
          </Tooltip>
        );
      },
    },
    {
      key: "confidence",
      header: "Confidence",
      width: "10%",
      render: (item: RecommendationSummary) => (
        <Tooltip
          content="Confidence reflects how certain the analysis is that this recommendation is correct."
          relationship="label"
        >
          <Badge appearance="tint" color="brand">
            {(item.confidenceScore * 100).toFixed(0)}%
          </Badge>
        </Tooltip>
      ),
    },
    {
      key: "trust",
      header: "Trust",
      width: "10%",
      render: (item: RecommendationSummary) => {
        const trust = Number(item.trustScore ?? item.confidenceScore ?? 0);
        const trustLevel = String(item.trustLevel ?? "medium").toUpperCase();
        const color =
          trustLevel === "HIGH"
            ? "success"
            : trustLevel === "LOW"
              ? "danger"
              : "warning";
        return (
          <Tooltip
            content="Trust blends confidence with evidence completeness and freshness to indicate how safe it is to rely on this recommendation now."
            relationship="description"
          >
            <Badge appearance="tint" color={color}>
              {Math.round(trust * 100)}% {trustLevel}
            </Badge>
          </Tooltip>
        );
      },
    },
    {
      key: "source",
      header: "Source",
      width: "8%",
      render: (item: RecommendationSummary) => (
        <Badge
          appearance="filled"
          color={
            item.source === "advisor"
              ? "brand"
              : item.source === "quick_review"
                ? "informative"
                : item.source === "psrule"
                  ? "success"
                  : item.source === "drift"
                    ? "danger"
                    : "warning"
          }
          size="small"
        >
          {normalizeSourceLabel(item)}
        </Badge>
      ),
    },
    {
      key: "approvals",
      header: "Approvals",
      width: "10%",
      render: (item: RecommendationSummary) => (
        <Text size={300}>
          {item.receivedApprovals}/{item.requiredApprovals}
        </Text>
      ),
    },
  ];

  return (
    <div className={styles.container}>
      <AzurePageHeader
        title="Recommendations"
        subtitle="AI-generated governance recommendations with dual-control approval"
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
          </>
        }
      >
        <div className={styles.filterBar}>
          <Badge
            appearance={status === undefined ? "filled" : "outline"}
            color="brand"
            {...statusBadgeProps(undefined)}
          >
            All
          </Badge>
          <Badge
            appearance={status === pendingStatusFilter ? "filled" : "outline"}
            color="warning"
            {...statusBadgeProps(pendingStatusFilter)}
          >
            Pending
          </Badge>
          <Badge
            appearance={
              status === RECOMMENDATION_WORKFLOW_STATUS.approved
                ? "filled"
                : "outline"
            }
            color="success"
            {...statusBadgeProps(RECOMMENDATION_WORKFLOW_STATUS.approved)}
          >
            Approved
          </Badge>
          <Badge
            appearance={
              status === RECOMMENDATION_WORKFLOW_STATUS.rejected
                ? "filled"
                : "outline"
            }
            color="danger"
            {...statusBadgeProps(RECOMMENDATION_WORKFLOW_STATUS.rejected)}
          >
            Rejected
          </Badge>
          <Badge
            appearance={
              status === RECOMMENDATION_WORKFLOW_STATUS.planned
                ? "filled"
                : "outline"
            }
            color="brand"
            {...statusBadgeProps(RECOMMENDATION_WORKFLOW_STATUS.planned)}
          >
            Planned
          </Badge>
          <Badge
            appearance={
              status === RECOMMENDATION_WORKFLOW_STATUS.inProgress
                ? "filled"
                : "outline"
            }
            color="informative"
            {...statusBadgeProps(RECOMMENDATION_WORKFLOW_STATUS.inProgress)}
          >
            In Progress
          </Badge>
          <Badge
            appearance={
              status === RECOMMENDATION_WORKFLOW_STATUS.verified
                ? "filled"
                : "outline"
            }
            color="success"
            {...statusBadgeProps(RECOMMENDATION_WORKFLOW_STATUS.verified)}
          >
            Verified
          </Badge>
          <Select
            value={orderBy}
            onChange={(_, data) => setOrderBy(data.value)}
            aria-label="Sort recommendations"
          >
            <option value="risk-weighted">Sort: Queue score</option>
            <option value="risk">Sort: Risk</option>
            <option value="confidence">Sort: Confidence</option>
          </Select>
          <Button
            appearance="secondary"
            className={styles.filterToggle}
            onClick={() => setFiltersOpen((prev) => !prev)}
          >
            {filtersOpen ? "Hide filters" : "Show filters"}
          </Button>
          <Button
            appearance="secondary"
            disabled={selectedCount === 0 || bulkBusy}
            onClick={() => void handleBulkDecision("approve")}
          >
            {bulkBusy ? "Working..." : `Approve selected (${selectedCount})`}
          </Button>
          <Button
            appearance="secondary"
            disabled={selectedCount === 0 || bulkBusy}
            onClick={() => void handleBulkDecision("reject")}
          >
            {bulkBusy ? "Working..." : `Reject selected (${selectedCount})`}
          </Button>
          {analysisRunFilter && (
            <Badge
              appearance="filled"
              color="brand"
              onClick={() => {
                const next = new URLSearchParams(searchParams);
                next.delete("analysisRunId");
                setSearchParams(next, { replace: true });
              }}
              style={{ cursor: "pointer" }}
            >
              Run: {analysisRunFilter.slice(0, 8)}... (clear)
            </Badge>
          )}
          {serviceGroupFilter && (
            <Badge
              appearance="filled"
              color="brand"
              onClick={() => {
                const next = new URLSearchParams(searchParams);
                next.delete("serviceGroupId");
                setSearchParams(next, { replace: true });
              }}
              style={{ cursor: "pointer" }}
            >
              Service Group: {serviceGroupFilter.slice(0, 8)}... (clear)
            </Badge>
          )}
        </div>
        {filtersOpen && (
          <div className={styles.filterPanel}>
            <div className={styles.filterBlock}>
              <Text className={styles.filterLabel}>Search</Text>
              <Input
                aria-label="Search recommendations"
                value={searchQuery}
                onChange={(_, data) => setSearchQuery(data.value)}
                placeholder="Resource, title, or action"
              />
            </div>
            <div className={styles.filterBlock}>
              <Text className={styles.filterLabel}>Source</Text>
              <Select
                value={sourceFilter}
                onChange={(_, data) => setSourceFilter(data.value)}
                aria-label="Filter by recommendation source"
              >
                <option value="">All sources</option>
                <option value="advisor">Advisor</option>
                <option value="quick_review">Quick Review</option>
                <option value="psrule">PSRule</option>
                <option value="drift">Drift</option>
                <option value="ai_synthesis">AI Synthesis</option>
              </Select>
            </div>
            <div className={styles.filterBlock}>
              <Text className={styles.filterLabel}>Trust level</Text>
              <Select
                value={trustFilter}
                onChange={(_, data) => setTrustFilter(data.value)}
                aria-label="Filter by trust level"
              >
                <option value="">All trust levels</option>
                <option value="high">High trust</option>
                <option value="medium">Medium trust</option>
                <option value="low">Low trust</option>
              </Select>
            </div>
            <div className={styles.filterBlock}>
              <Text className={styles.filterLabel}>Category</Text>
              <Select
                value={categoryFilter}
                onChange={(_, data) => setCategoryFilter(data.value)}
                aria-label="Filter by recommendation category"
              >
                <option value="">All categories</option>
                {categoryOptions.map((category) => (
                  <option key={category} value={category}>
                    {formatCategory(category)}
                  </option>
                ))}
              </Select>
            </div>
            <div className={styles.filterBlock}>
              <Text className={styles.filterLabel}>Confidence band</Text>
              <Select
                value={confidenceBandFilter}
                onChange={(_, data) => setConfidenceBandFilter(data.value)}
                aria-label="Filter by confidence band"
              >
                <option value="">All bands</option>
                <option value="high">High (80%+)</option>
                <option value="medium">Medium (60-79%)</option>
                <option value="low">Low (&lt;60%)</option>
              </Select>
            </div>
            <div className={styles.filterBlock}>
              <Text className={styles.filterLabel}>Queue band</Text>
              <Select
                value={queueBandFilter}
                onChange={(_, data) => setQueueBandFilter(data.value)}
                aria-label="Filter by queue band"
              >
                <option value="">All bands</option>
                <option value="high">High (80%+)</option>
                <option value="medium">Medium (60-79%)</option>
                <option value="low">Low (&lt;60%)</option>
              </Select>
            </div>
            <div className={styles.filterBlock}>
              <Text className={styles.filterLabel}>Freshness age</Text>
              <Select
                value={freshnessBandFilter}
                onChange={(_, data) => setFreshnessBandFilter(data.value)}
                aria-label="Filter by freshness age"
              >
                <option value="">All ages</option>
                <option value="fresh">Fresh (0-6 days)</option>
                <option value="aging">Aging (7-29 days)</option>
                <option value="stale">Stale (30+ days)</option>
              </Select>
            </div>
            <div className={styles.filterActions}>
              <Button
                appearance="secondary"
                onClick={() => {
                  setSearchQuery("");
                  setSourceFilter("");
                  setTrustFilter("");
                  setCategoryFilter("");
                  setConfidenceBandFilter("");
                  setQueueBandFilter("");
                  setFreshnessBandFilter("");
                }}
              >
                Clear filters
              </Button>
            </div>
          </div>
        )}
      </AzurePageHeader>

      <Card className={styles.scoreExplainerCard}>
        <Text size={500} weight="semibold" block>
          How scores are interpreted
        </Text>
        <div className={styles.scoreExplainerGrid}>
          <div>
            <Text className={styles.scoreExplainerTitle} block>
              Confidence
            </Text>
            <Text className={styles.scoreExplainerBody} block>
              Indicates how certain the analysis is that the recommendation is
              correct.
            </Text>
            <Text className={styles.scoreExplainerMeta} block>
              Bands: High &ge; 80% • Medium 60-79% • Low &lt; 60%
            </Text>
          </div>
          <div>
            <Text className={styles.scoreExplainerTitle} block>
              Trust
            </Text>
            <Text className={styles.scoreExplainerBody} block>
              Blends confidence with evidence completeness and data freshness to
              show how safe it is to rely on the recommendation now.
            </Text>
            <Text className={styles.scoreExplainerMeta} block>
              Trust level summarizes the score for faster triage.
            </Text>
          </div>
          <div>
            <Text className={styles.scoreExplainerTitle} block>
              Queue score
            </Text>
            <Text className={styles.scoreExplainerBody} block>
              Risk-weighted signal used to prioritize the recommendation queue.
            </Text>
            <Text className={styles.scoreExplainerMeta} block>
              Higher scores should be triaged sooner.
            </Text>
          </div>
        </div>
      </Card>

      <Card className={styles.summaryCard}>
        <CardHeader
          image={<Sparkle20Regular />}
          header={<Text weight="semibold">AI Summary</Text>}
          action={
            <div className={styles.summaryHeaderActions}>
              {summary.text && (
                <Button
                  appearance="subtle"
                  size="small"
                  onClick={() => setSummaryExpanded((prev) => !prev)}
                >
                  {summaryExpanded ? "Show less" : "Show more"}
                </Button>
              )}
              <Button
                appearance="subtle"
                size="small"
                icon={<Sparkle20Regular />}
                disabled={summary.streaming}
                onClick={() => {
                  summaryGenerationTokenRef.current = refreshToken;
                  void summary.generate({ accessToken });
                }}
              >
                {summary.text ? "Regenerate" : "Generate Summary"}
              </Button>
            </div>
          }
        />
        <div className={styles.summaryMetaRow}>
          <Badge appearance="tint" color="brand">
            {summaryInsights.total} total
          </Badge>
          <Badge appearance="tint" color="warning">
            {summaryInsights.pending} pending
          </Badge>
          <Badge appearance="tint" color="danger">
            {summaryInsights.highPriority} high priority
          </Badge>
          <Badge appearance="outline">
            {summaryInsights.categories} categories
          </Badge>
        </div>
        {summary.streaming && !summary.text && (
          <Spinner size="tiny" label="Generating summary..." />
        )}
        {summary.streaming && summary.text && (
          <Text className={styles.subtle} size={200}>
            Updating summary…
          </Text>
        )}
        {summary.text && (
          <div
            className={`${styles.summaryViewport} ${summaryExpanded ? styles.summaryViewportExpanded : styles.summaryViewportCollapsed}`}
          >
            <div className={styles.summaryText}>{summaryBlocks}</div>
          </div>
        )}
        {summary.error && (
          <Text style={{ color: tokens.colorPaletteRedForeground1 }}>
            {summary.error}
          </Text>
        )}
      </Card>

      {!loading && !error && displayedItems.length > 0 && (
        <div className={styles.spotlightGrid}>
          <Card className={styles.spotlightCard}>
            <div className={styles.spotlightHeader}>
              <Text weight="semibold">Priority Inbox</Text>
              <Badge appearance="tint" color="warning">
                {priorityInbox.doNow.length +
                  priorityInbox.thisWeek.length +
                  priorityInbox.backlog.length}{" "}
                queued
              </Badge>
            </div>
            <div className={styles.inboxColumns}>
              <div className={styles.inboxColumn}>
                <Text size={300} weight="semibold">
                  Do now
                </Text>
                <div className={styles.inboxList}>
                  {priorityInbox.doNow.length === 0 && (
                    <Text size={200}>No urgent items.</Text>
                  )}
                  {priorityInbox.doNow.map((item) => (
                    <div key={item.id} className={styles.inboxItem}>
                      <Button
                        appearance="transparent"
                        size="small"
                        onClick={() => handleItemClick(item)}
                      >
                        {item.title ?? item.recommendationType}
                      </Button>
                      <Badge
                        color={badgeColor(item.priority)}
                        appearance="filled"
                      >
                        {item.priority}
                      </Badge>
                    </div>
                  ))}
                </div>
              </div>
              <div className={styles.inboxColumn}>
                <Text size={300} weight="semibold">
                  This week
                </Text>
                <div className={styles.inboxList}>
                  {priorityInbox.thisWeek.length === 0 && (
                    <Text size={200}>No medium-term items.</Text>
                  )}
                  {priorityInbox.thisWeek.map((item) => (
                    <div key={item.id} className={styles.inboxItem}>
                      <Button
                        appearance="transparent"
                        size="small"
                        onClick={() => handleItemClick(item)}
                      >
                        {item.title ?? item.recommendationType}
                      </Button>
                      <Badge appearance="outline">
                        {Math.round(Number(item.riskWeightedScore ?? 0) * 100)}%
                      </Badge>
                    </div>
                  ))}
                </div>
              </div>
              <div className={styles.inboxColumn}>
                <Text size={300} weight="semibold">
                  Backlog
                </Text>
                <div className={styles.inboxList}>
                  {priorityInbox.backlog.length === 0 && (
                    <Text size={200}>No backlog items.</Text>
                  )}
                  {priorityInbox.backlog.map((item) => (
                    <div key={item.id} className={styles.inboxItem}>
                      <Button
                        appearance="transparent"
                        size="small"
                        onClick={() => handleItemClick(item)}
                      >
                        {item.title ?? item.recommendationType}
                      </Button>
                      <Badge appearance="outline">{item.priority}</Badge>
                    </div>
                  ))}
                </div>
              </div>
            </div>
          </Card>

          <Card className={styles.spotlightCard}>
            <div className={styles.spotlightHeader}>
              <Text weight="semibold">Business Impact View</Text>
              <Badge appearance="tint" color="brand">
                Pending only
              </Badge>
            </div>
            <div className={styles.insightRow}>
              <Text>Outage risk items</Text>
              <Badge
                color={businessImpact.outageRisk > 0 ? "danger" : "success"}
              >
                {businessImpact.outageRisk}
              </Badge>
            </div>
            <div className={styles.insightRow}>
              <Text>Compliance exposure items</Text>
              <Badge
                color={
                  businessImpact.complianceExposure > 0 ? "warning" : "success"
                }
              >
                {businessImpact.complianceExposure}
              </Badge>
            </div>
            <div className={styles.insightRow}>
              <Text>Monthly cost opportunity</Text>
              <Text weight="semibold">
                ${businessImpact.monthlyCostOpportunity.toFixed(0)}
              </Text>
            </div>
            <div className={styles.insightRow}>
              <Text>Sustainability opportunities</Text>
              <Badge
                color={
                  businessImpact.sustainabilityOpportunity > 0
                    ? "brand"
                    : "success"
                }
              >
                {businessImpact.sustainabilityOpportunity}
              </Badge>
            </div>
            <div className={styles.insightRow}>
              <Text>New since previous run</Text>
              <Badge
                appearance="tint"
                color={runDelta.newItems > 0 ? "warning" : "success"}
              >
                {runDelta.newItems}
              </Badge>
            </div>
            <div className={styles.insightRow}>
              <Text>Resolved since previous run</Text>
              <Badge
                appearance="tint"
                color={runDelta.resolvedItems > 0 ? "success" : "brand"}
              >
                {runDelta.resolvedItems}
              </Badge>
            </div>
          </Card>
        </div>
      )}

      {loading && (
        <div
          style={{ padding: tokens.spacingHorizontalXXL, textAlign: "center" }}
        >
          <Spinner label="Loading recommendations..." />
        </div>
      )}

      {!loading && error && (
        <div style={{ padding: tokens.spacingHorizontalXXL }}>
          <Text
            weight="semibold"
            block
            style={{ marginBottom: tokens.spacingVerticalS }}
          >
            Failed to load recommendations
          </Text>
          <Text style={{ color: tokens.colorNeutralForeground3 }}>{error}</Text>
        </div>
      )}

      {!loading && !error && (
        <AzureList
          columns={columns}
          items={displayedItems}
          onItemClick={handleItemClick}
          emptyMessage="No recommendations found. Run an analysis to generate recommendations."
        />
      )}

      {!loading && !error && displayedItems.length > 0 && hasMore && (
        <div
          style={{ padding: tokens.spacingHorizontalXXL, textAlign: "center" }}
        >
          <Button
            appearance="secondary"
            disabled={pageLoading}
            onClick={() => setOffset((prev) => prev + pageSize)}
          >
            {pageLoading ? "Loading..." : "Load more"}
          </Button>
        </div>
      )}
    </div>
  );
}
