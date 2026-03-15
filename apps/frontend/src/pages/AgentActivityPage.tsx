/**
 * AgentActivityPage — AG-UI streaming orchestration view.
 *
 * Demonstrates that AG-UI + Microsoft Agent Framework is not limited to chat:
 * selecting a service group triggers a live multi-agent analysis run and
 * renders each NimbusIQ agent's execution (DriftDetection, BestPractice,
 * WellArchitected, FinOps, ServiceHierarchy) as a real-time tool-call
 * timeline so operators can see exactly what is being evaluated.
 *
 * Backend: POST /api/v1/agents/analysis-stream/{serviceGroupId}
 */

import { useCallback, useEffect, useState } from "react";
import {
  makeStyles,
  tokens,
  Text,
  Button,
  Badge,
  Spinner,
  mergeClasses,
  shorthands,
} from "@fluentui/react-components";
import {
  ChartMultiple24Regular,
  CheckmarkCircle24Regular,
  DismissCircle24Regular,
  ArrowSync24Regular,
  DataUsage24Regular,
  ArrowReset24Regular,
} from "@fluentui/react-icons";
import { AzurePageHeader } from "../components/AzurePageHeader";
import { useAccessToken } from "../auth/useAccessToken";
import { useServiceGroupDiscovery } from "../hooks/useServiceGroupDiscovery";
import { useAgentAnalysis } from "../hooks/useAgentAnalysis";
import type { AgentExecution } from "../hooks/useAgentAnalysis";
import type { ServiceGroup } from "../services/controlPlaneApi";

// ─── Styles ───────────────────────────────────────────────────────────────────

const useStyles = makeStyles({
  page: {
    display: "flex",
    flexDirection: "column",
    height: "100%",
    overflow: "hidden",
  },
  body: {
    flex: 1,
    overflow: "hidden",
    display: "flex",
    padding: tokens.spacingHorizontalM,
    gap: tokens.spacingHorizontalM,
  },
  // Left panel — service group selector
  leftPanel: {
    width: "260px",
    flexShrink: 0,
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalS,
    overflowY: "auto",
  },
  panelCard: {
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: tokens.spacingHorizontalM,
  },
  panelTitle: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground1,
    marginBottom: tokens.spacingVerticalS,
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalXS,
  },
  groupList: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalXS,
  },
  groupItem: {
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    borderRadius: tokens.borderRadiusMedium,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    cursor: "pointer",
    backgroundColor: tokens.colorNeutralBackground1,
    color: tokens.colorNeutralForeground1,
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalS,
    transition: "all 0.15s ease",
    "&:hover": {
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },
    "&:focus-visible": {
      outline: `2px solid ${tokens.colorBrandForeground1}`,
      outlineOffset: "2px",
    },
  },
  groupItemActive: {
    backgroundColor: tokens.colorBrandBackground2Hover,
    ...shorthands.borderColor(tokens.colorBrandStroke1),
    color: tokens.colorNeutralForeground1,
    "&:hover": {
      backgroundColor: tokens.colorBrandBackground2Pressed,
    },
  },
  groupIcon: {
    fontSize: "16px",
    flexShrink: 0,
    color: tokens.colorBrandForeground1,
  },
  groupIconActive: {
    color: tokens.colorBrandForeground1,
  },
  groupName: {
    fontSize: tokens.fontSizeBase200,
    color: "inherit",
    overflow: "hidden",
    textOverflow: "ellipsis",
    whiteSpace: "nowrap",
  },
  // Main content
  mainPanel: {
    flex: 1,
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalM,
    overflow: "hidden",
  },
  scrollable: {
    flex: 1,
    overflowY: "auto",
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalM,
  },
  // Agent run header
  runHeader: {
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: tokens.spacingHorizontalM,
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    flexWrap: "wrap",
    gap: tokens.spacingHorizontalS,
  },
  runMeta: {
    display: "flex",
    flexDirection: "column",
    gap: "2px",
  },
  runTitle: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground1,
  },
  runSubtitle: {
    fontSize: tokens.fontSizeBase100,
    fontFamily: tokens.fontFamilyMonospace,
    color: tokens.colorNeutralForeground3,
  },
  // Agent cards
  agentList: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalS,
  },
  agentCard: {
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    overflow: "hidden",
    transition: "border-color 0.2s ease",
  },
  agentCardRunning: {
    ...shorthands.borderColor(tokens.colorBrandForeground1),
  },
  agentCardCompleted: {
    ...shorthands.borderColor(tokens.colorPaletteGreenBorder2),
  },
  agentCardFailed: {
    ...shorthands.borderColor(tokens.colorPaletteRedBorder2),
  },
  agentCardHeader: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    cursor: "pointer",
    "&:hover": {
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },
  },
  agentNameText: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground1,
    flex: 1,
  },
  agentToolText: {
    fontSize: tokens.fontSizeBase100,
    fontFamily: tokens.fontFamilyMonospace,
    color: tokens.colorBrandForeground1,
    backgroundColor: tokens.colorNeutralBackground2,
    padding: `1px 6px`,
    borderRadius: tokens.borderRadiusMedium,
  },
  agentElapsed: {
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground3,
  },
  agentOutput: {
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  outputPre: {
    fontSize: tokens.fontSizeBase100,
    fontFamily: tokens.fontFamilyMonospace,
    color: tokens.colorNeutralForeground2,
    overflowX: "auto",
    margin: 0,
    maxHeight: "200px",
    overflowY: "auto",
    lineHeight: "1.5",
  },
  // Summary
  summaryCard: {
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: tokens.spacingHorizontalM,
  },
  summaryTitle: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground1,
    marginBottom: tokens.spacingVerticalXS,
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalXS,
  },
  summaryText: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
    lineHeight: tokens.lineHeightBase400,
    whiteSpace: "pre-wrap",
  },
  cursorBlink: {
    display: "inline-block",
    width: "2px",
    height: "1em",
    backgroundColor: tokens.colorBrandForeground1,
    marginLeft: "1px",
    verticalAlign: "text-bottom",
    animationName: {
      "0%": { opacity: 1 },
      "50%": { opacity: 0 },
      "100%": { opacity: 1 },
    },
    animationDuration: "1s",
    animationIterationCount: "infinite",
  },
  // Snapshot (right rail)
  rightPanel: {
    width: "240px",
    flexShrink: 0,
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalS,
    overflowY: "auto",
  },
  snapshotCard: {
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: tokens.spacingHorizontalM,
  },
  snapshotTitle: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground1,
    marginBottom: tokens.spacingVerticalXS,
  },
  snapshotRow: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "baseline",
    paddingBlock: "2px",
    fontSize: tokens.fontSizeBase100,
    color: tokens.colorNeutralForeground2,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    "&:last-child": {
      borderBottom: "none",
    },
  },
  snapshotValue: {
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
    fontFamily: tokens.fontFamilyMonospace,
  },
  // Empty state
  emptyState: {
    flex: 1,
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    justifyContent: "center",
    gap: tokens.spacingVerticalM,
    color: tokens.colorNeutralForeground3,
    textAlign: "center",
    padding: tokens.spacingHorizontalXXL,
  },
  emptyIcon: {
    fontSize: "48px",
    opacity: 0.4,
  },
  errorBanner: {
    backgroundColor: tokens.colorPaletteRedBackground1,
    border: `1px solid ${tokens.colorPaletteRedBorder2}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: tokens.spacingHorizontalM,
    color: tokens.colorPaletteRedForeground1,
    fontSize: tokens.fontSizeBase200,
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalS,
  },
  protocolChip: {
    display: "inline-flex",
    alignItems: "center",
    gap: "4px",
    fontSize: tokens.fontSizeBase100,
    fontFamily: tokens.fontFamilyMonospace,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusCircular,
    padding: `2px ${tokens.spacingHorizontalXS}`,
    color: tokens.colorBrandForeground1,
    marginTop: tokens.spacingVerticalXS,
  },
  // Skeleton loaders for streaming startup
  "@keyframes shimmer": {
    "0%": { backgroundPosition: "-200% 0" },
    "100%": { backgroundPosition: "200% 0" },
  },
  skeletonCard: {
    borderRadius: tokens.borderRadiusMedium,
    padding: tokens.spacingHorizontalM,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
    display: "flex",
    flexDirection: "column" as const,
    gap: tokens.spacingVerticalS,
  },
  skeletonRow: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalS,
  },
  skeletonCircle: {
    width: "32px",
    height: "32px",
    borderRadius: "50%",
    background: `linear-gradient(90deg, ${tokens.colorNeutralBackground3} 25%, ${tokens.colorNeutralBackground4} 50%, ${tokens.colorNeutralBackground3} 75%)`,
    backgroundSize: "200% 100%",
    animationName: "shimmer",
    animationDuration: "1.5s",
    animationIterationCount: "infinite",
    animationTimingFunction: "ease-in-out",
  },
  skeletonLine: {
    height: "12px",
    borderRadius: tokens.borderRadiusSmall,
    background: `linear-gradient(90deg, ${tokens.colorNeutralBackground3} 25%, ${tokens.colorNeutralBackground4} 50%, ${tokens.colorNeutralBackground3} 75%)`,
    backgroundSize: "200% 100%",
    animationName: "shimmer",
    animationDuration: "1.5s",
    animationIterationCount: "infinite",
    animationTimingFunction: "ease-in-out",
  },
  skeletonLineShort: {
    width: "60%",
  },
  skeletonLineMedium: {
    width: "80%",
  },
  skeletonLineFull: {
    width: "100%",
  },
  // ─── Conversation bubble styles ───────────────────────────────────────────
  conversationWrapper: {
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  bubbleRow: {
    display: "flex",
    gap: tokens.spacingHorizontalS,
    alignItems: "flex-start",
  },
  bubbleAvatar: {
    fontSize: "18px",
    width: "34px",
    height: "34px",
    display: "flex",
    alignItems: "center",
    justifyContent: "center",
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: "50%",
    flexShrink: 0,
  },
  bubble: {
    flex: 1,
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: `0 ${tokens.borderRadiusMedium} ${tokens.borderRadiusMedium} ${tokens.borderRadiusMedium}`,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    display: "flex",
    flexDirection: "column",
    gap: "4px",
    overflow: "hidden",
  },
  bubbleSender: {
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorBrandForeground1,
  },
  bubbleText: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
    lineHeight: tokens.lineHeightBase400,
  },
  bubbleMetricsRow: {
    display: "flex",
    flexWrap: "wrap",
    gap: "4px",
    marginTop: "2px",
  },
  bubbleMetricChip: {
    display: "inline-flex",
    alignItems: "center",
    gap: "4px",
    backgroundColor: tokens.colorBrandBackground2,
    border: `1px solid ${tokens.colorBrandStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: `1px ${tokens.spacingHorizontalXS}`,
    fontSize: tokens.fontSizeBase100,
  },
  bubbleMetricLabel: {
    color: tokens.colorNeutralForeground3,
  },
  bubbleMetricValue: {
    color: tokens.colorNeutralForeground1,
    fontWeight: tokens.fontWeightSemibold,
    fontFamily: tokens.fontFamilyMonospace,
  },
  bubbleFindingList: {
    margin: 0,
    paddingLeft: tokens.spacingHorizontalL,
    display: "flex",
    flexDirection: "column",
    gap: "2px",
    marginTop: "2px",
  },
  bubbleFindingItem: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground2,
    lineHeight: tokens.lineHeightBase400,
  },
});

// ─── Agent name → human label mapping ────────────────────────────────────────

const AGENT_LABELS: Record<string, { label: string; emoji: string }> = {
  DriftDetectionAgent: { label: "Drift Detection", emoji: "📈" },
  BestPracticeEngine: { label: "Best Practice Engine", emoji: "🛡️" },
  WellArchitectedAssessmentAgent: { label: "Well-Architected", emoji: "🏛️" },
  FinOpsOptimizerAgent: { label: "FinOps Optimizer", emoji: "💰" },
  ServiceHierarchyAnalyzer: { label: "Service Hierarchy", emoji: "🗂️" },
  ReliabilityAgent: { label: "Reliability Agent", emoji: "🔄" },
  GovernanceNegotiationAgent: { label: "Governance Negotiation", emoji: "⚖️" },
  GovernanceMediatorAgent: { label: "Governance Mediator", emoji: "🤝" },
  ArchitectureAgent: { label: "Architecture Agent", emoji: "🏗️" },
  SustainabilityAgent: { label: "Sustainability Agent", emoji: "🌱" },
};

function getAgentMeta(agentName: string) {
  return AGENT_LABELS[agentName] ?? { label: agentName, emoji: "🤖" };
}

// ─── Agent output parser ─────────────────────────────────────────────────────

/** Extract readable content from agent JSON output for conversation display. */
function parseAgentOutput(raw: string): {
  summary?: string;
  metrics: { label: string; value: string }[];
  items: string[];
} {
  try {
    const obj = JSON.parse(raw) as Record<string, unknown>;
    const summary =
      typeof obj.summary === "string"
        ? obj.summary
        : typeof obj.message === "string"
          ? obj.message
          : typeof obj.description === "string"
            ? obj.description
            : undefined;

    const metrics: { label: string; value: string }[] = [];
    const numericKeys = [
      "score",
      "driftScore",
      "totalViolations",
      "criticalViolations",
      "recommendationCount",
      "costSavings",
      "confidenceScore",
      "complianceScore",
    ];
    for (const k of numericKeys) {
      if (typeof obj[k] === "number") {
        const label = k
          .replace(/([A-Z])/g, " $1")
          .replace(/^./, (s) => s.toUpperCase());
        metrics.push({ label, value: String(obj[k]) });
      }
    }

    const items: string[] = [];
    for (const k of [
      "findings",
      "recommendations",
      "violations",
      "insights",
      "issues",
      "results",
    ]) {
      if (Array.isArray(obj[k])) {
        const arr = obj[k] as unknown[];
        for (const item of arr.slice(0, 4)) {
          if (typeof item === "string") {
            items.push(item);
          } else if (typeof item === "object" && item !== null) {
            const i = item as Record<string, unknown>;
            const text =
              i.message ?? i.title ?? i.ruleName ?? i.description ?? i.name;
            if (typeof text === "string") items.push(text);
          }
        }
        break;
      }
    }

    return { summary, metrics, items };
  } catch {
    return { metrics: [], items: [] };
  }
}

// ─── AgentOutputConversation ──────────────────────────────────────────────────

/** Renders parsed agent output as a readable conversation bubble. */
function AgentOutputConversation({ agent }: { agent: AgentExecution }) {
  const styles = useStyles();
  const { emoji, label } = getAgentMeta(agent.agentName);

  if (!agent.output) return null;

  const { summary, metrics, items } = parseAgentOutput(agent.output);
  const hasContent =
    summary !== undefined || metrics.length > 0 || items.length > 0;

  return (
    <div className={styles.conversationWrapper}>
      <div className={styles.bubbleRow}>
        <span className={styles.bubbleAvatar} aria-hidden="true">
          {emoji}
        </span>
        <div className={styles.bubble}>
          <span className={styles.bubbleSender}>{label}</span>
          {summary && <span className={styles.bubbleText}>{summary}</span>}
          {metrics.length > 0 && (
            <div className={styles.bubbleMetricsRow}>
              {metrics.map((m) => (
                <span key={m.label} className={styles.bubbleMetricChip}>
                  <span className={styles.bubbleMetricLabel}>{m.label}</span>
                  <span className={styles.bubbleMetricValue}>{m.value}</span>
                </span>
              ))}
            </div>
          )}
          {items.length > 0 && (
            <ul className={styles.bubbleFindingList}>
              {items.map((it, idx) => (
                // biome-ignore lint/suspicious/noArrayIndexKey: stable ordered list from parsed output
                <li key={idx} className={styles.bubbleFindingItem}>
                  {it}
                </li>
              ))}
            </ul>
          )}
          {!hasContent && (
            <span
              className={styles.bubbleText}
              style={{ color: tokens.colorNeutralForeground3 }}
            >
              Analysis complete.
            </span>
          )}
        </div>
      </div>
    </div>
  );
}

// ─── AgentCard ────────────────────────────────────────────────────────────────

function AgentCard({ agent }: { agent: AgentExecution }) {
  const styles = useStyles();
  const [expanded, setExpanded] = useState(false);
  const { label, emoji } = getAgentMeta(agent.agentName);

  const cardClass = mergeClasses(
    styles.agentCard,
    agent.status === "running" && styles.agentCardRunning,
    agent.status === "completed" && styles.agentCardCompleted,
    agent.status === "failed" && styles.agentCardFailed,
  );

  return (
    <div className={cardClass} role="region" aria-label={`${label} agent`}>
      <div
        className={styles.agentCardHeader}
        role="button"
        tabIndex={0}
        aria-expanded={expanded}
        onClick={() => agent.status !== "running" && setExpanded(!expanded)}
        onKeyDown={(e) => {
          if (
            (e.key === "Enter" || e.key === " ") &&
            agent.status !== "running"
          ) {
            setExpanded(!expanded);
          }
        }}
        aria-label={`${label}: ${agent.status}. ${expanded ? "Collapse" : "Expand"} result.`}
      >
        <span aria-hidden="true" style={{ fontSize: "18px" }}>
          {emoji}
        </span>
        <span className={styles.agentNameText}>{label}</span>
        <code className={styles.agentToolText}>{agent.toolName}()</code>
        {agent.elapsedMs !== undefined && (
          <span className={styles.agentElapsed}>{agent.elapsedMs}ms</span>
        )}
        {agent.status === "running" && (
          <Spinner size="extra-small" aria-label="Running" />
        )}
        {agent.status === "completed" && (
          <CheckmarkCircle24Regular
            aria-label="Completed"
            style={{ color: tokens.colorPaletteGreenForeground1 }}
          />
        )}
        {agent.status === "failed" && (
          <DismissCircle24Regular
            aria-label="Failed"
            style={{ color: tokens.colorPaletteRedForeground1 }}
          />
        )}
      </div>

      {expanded && agent.output && <AgentOutputConversation agent={agent} />}
    </div>
  );
}

// ─── SnapshotPanel ────────────────────────────────────────────────────────────

function SnapshotPanel({ snapshot }: { snapshot: Record<string, unknown> }) {
  const styles = useStyles();

  // Flatten one level of interesting keys for display
  const rows: Array<[string, string]> = Object.entries(snapshot)
    .filter(([, v]) => typeof v !== "object" || v === null)
    .map(([k, v]) => [
      // camelCase → Title Case
      k.replace(/([A-Z])/g, " $1").replace(/^./, (s) => s.toUpperCase()),
      String(v),
    ]);

  if (rows.length === 0) return null;

  return (
    <div
      className={styles.snapshotCard}
      role="region"
      aria-label="Snapshot metrics"
    >
      <Text className={styles.snapshotTitle as string} as="h3">
        Analysis Snapshot
      </Text>
      {rows.slice(0, 12).map(([key, value]) => (
        <div key={key} className={styles.snapshotRow}>
          <span>{key}</span>
          <span className={styles.snapshotValue}>{value}</span>
        </div>
      ))}
    </div>
  );
}

// ─── AgentActivityPage ────────────────────────────────────────────────────────
// Compliance: All React hooks declared at top level per Rules of Hooks (FW-001)

export function AgentActivityPage() {
  const styles = useStyles();
  // Hooks at top level before any conditionals
  const { accessToken } = useAccessToken();
  const { serviceGroups, loading: groupsLoading } =
    useServiceGroupDiscovery(accessToken);
  const { state, startAnalysis, reset } = useAgentAnalysis();

  const [selectedGroup, setSelectedGroup] = useState<ServiceGroup | null>(null);

  useEffect(() => {
    document.title = "NimbusIQ — Agent Activity";
  }, []);

  const handleSelectGroup = useCallback(
    (group: ServiceGroup) => {
      if (state.streaming) return; // don't switch mid-run
      setSelectedGroup(group);
      void startAnalysis(group.id, { accessToken: accessToken ?? undefined });
    },
    [state.streaming, startAnalysis, accessToken],
  );

  const handleReset = useCallback(() => {
    setSelectedGroup(null);
    reset();
  }, [reset]);

  const hasRun = state.agents.length > 0 || state.streaming;

  return (
    <div className={styles.page}>
      <AzurePageHeader
        title="Agent Orchestration"
        subtitle="Trigger live multi-agent analysis runs — watch each NimbusIQ agent execute in real time"
        breadcrumbs={
          <>
            <a
              href="/dashboard"
              style={{ color: "inherit", textDecoration: "none" }}
            >
              Home
            </a>
            <span aria-hidden="true"> / </span>
            <span>Agent Orchestration</span>
          </>
        }
      />

      <div className={styles.body}>
        {/* ── Left: Service Group selector ──────────────────────────────── */}
        <aside className={styles.leftPanel} aria-label="Service group selector">
          <div className={styles.panelCard}>
            <div className={styles.panelTitle}>
              <DataUsage24Regular aria-hidden="true" />
              Service Groups
            </div>

            {groupsLoading && <Spinner size="small" label="Loading…" />}

            {!groupsLoading && serviceGroups.length === 0 && (
              <Text
                style={{
                  fontSize: tokens.fontSizeBase100,
                  color: tokens.colorNeutralForeground3,
                }}
              >
                No service groups found. Discover from Azure on the Groups page.
              </Text>
            )}

            <div className={styles.groupList} role="list">
              {serviceGroups.map((group) => (
                <div
                  key={group.id}
                  role="listitem"
                  className={mergeClasses(
                    styles.groupItem,
                    selectedGroup?.id === group.id && styles.groupItemActive,
                  )}
                  tabIndex={0}
                  aria-pressed={selectedGroup?.id === group.id}
                  aria-label={`Run analysis on ${group.name}`}
                  onClick={() => handleSelectGroup(group)}
                  onKeyDown={(e) => {
                    if (e.key === "Enter" || e.key === " ")
                      handleSelectGroup(group);
                  }}
                >
                  <DataUsage24Regular
                    aria-hidden="true"
                    className={mergeClasses(
                      styles.groupIcon,
                      selectedGroup?.id === group.id && styles.groupIconActive,
                    )}
                  />
                  <span className={styles.groupName}>{group.name}</span>
                  {selectedGroup?.id === group.id && state.streaming && (
                    <Spinner size="extra-tiny" aria-label="Analysing" />
                  )}
                </div>
              ))}
            </div>
          </div>
        </aside>

        {/* ── Main: Agent execution timeline ────────────────────────────── */}
        <div className={styles.mainPanel}>
          {/* Run header */}
          {hasRun && (
            <div
              className={styles.runHeader}
              role="region"
              aria-label="Current run status"
            >
              <div className={styles.runMeta}>
                <span className={styles.runTitle}>
                  {state.serviceGroupName ??
                    selectedGroup?.name ??
                    "Analysis Run"}
                </span>
                {state.runId && (
                  <code className={styles.runSubtitle}>
                    run:{state.runId.slice(0, 8)}
                  </code>
                )}
              </div>
              <div
                style={{
                  display: "flex",
                  alignItems: "center",
                  gap: tokens.spacingHorizontalS,
                }}
              >
                {state.streaming ? (
                  <Badge
                    appearance="outline"
                    color="informative"
                    icon={<Spinner size="extra-small" />}
                  >
                    Running
                  </Badge>
                ) : (
                  <Badge
                    appearance="tint"
                    color={state.error ? "danger" : "success"}
                  >
                    {state.error ? "Failed" : "Complete"}
                  </Badge>
                )}
                <Button
                  size="small"
                  appearance="subtle"
                  icon={<ArrowReset24Regular />}
                  onClick={handleReset}
                  aria-label="Reset view"
                >
                  Reset
                </Button>
              </div>
            </div>
          )}

          {/* Error banner */}
          {state.error && (
            <div className={styles.errorBanner} role="alert">
              <DismissCircle24Regular aria-hidden="true" />
              {state.error}
            </div>
          )}

          {/* Empty state — no run yet */}
          {!hasRun && !state.error && (
            <div className={styles.emptyState} role="status" aria-live="polite">
              <ChartMultiple24Regular
                aria-hidden="true"
                className={styles.emptyIcon}
              />
              <Text weight="semibold" size={400}>
                Select a service group to start
              </Text>
              <Text
                size={200}
                style={{ color: tokens.colorNeutralForeground3 }}
              >
                Click any service group on the left to trigger a live
                multi-agent analysis run.
              </Text>
            </div>
          )}

          {/* Scrollable content */}
          {hasRun && (
            <div
              className={styles.scrollable}
              role="log"
              aria-live="polite"
              aria-label="Agent execution timeline"
            >
              {/* Skeleton loaders while streaming before first agent arrives */}
              {state.streaming && state.agents.length === 0 && (
                <div className={styles.agentList} aria-label="Loading agents">
                  {[1, 2, 3].map((i) => (
                    <div key={i} className={styles.skeletonCard}>
                      <div className={styles.skeletonRow}>
                        <div className={styles.skeletonCircle} />
                        <div
                          className={`${styles.skeletonLine} ${styles.skeletonLineMedium}`}
                        />
                      </div>
                      <div
                        className={`${styles.skeletonLine} ${styles.skeletonLineFull}`}
                      />
                      <div
                        className={`${styles.skeletonLine} ${styles.skeletonLineShort}`}
                      />
                    </div>
                  ))}
                </div>
              )}

              {/* Agent execution cards */}
              <div className={styles.agentList}>
                {state.agents.map((agent) => (
                  <AgentCard key={agent.id} agent={agent} />
                ))}
                {/* Placeholder for next agent while streaming */}
                {state.streaming &&
                  state.agents.length > 0 &&
                  state.agents.every((a) => a.status !== "running") && (
                    <div
                      className={styles.agentCard}
                      style={{
                        padding: tokens.spacingHorizontalM,
                        opacity: 0.5,
                      }}
                      aria-label="Waiting for next agent"
                    >
                      <Spinner
                        size="extra-small"
                        label="Waiting for next agent…"
                      />
                    </div>
                  )}
              </div>

              {/* Summary text (streamed) */}
              {(state.summary || state.summarising) && (
                <div
                  className={styles.summaryCard}
                  role="region"
                  aria-label="Analysis summary"
                >
                  <div className={styles.summaryTitle}>
                    <ArrowSync24Regular aria-hidden="true" />
                    Analysis Summary
                  </div>
                  <span className={styles.summaryText}>
                    {state.summary}
                    {state.summarising && (
                      <span className={styles.cursorBlink} aria-hidden="true" />
                    )}
                  </span>
                </div>
              )}
            </div>
          )}
        </div>

        {/* ── Right: State snapshot ──────────────────────────────────────── */}
        {state.snapshot && (
          <aside className={styles.rightPanel} aria-label="Analysis snapshot">
            <SnapshotPanel snapshot={state.snapshot} />
          </aside>
        )}
      </div>
    </div>
  );
}
