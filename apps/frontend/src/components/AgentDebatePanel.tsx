import React, { useEffect, useState } from "react";
import {
  Badge,
  Card,
  Divider,
  makeStyles,
  Spinner,
  Text,
  tokens,
  Tooltip,
} from "@fluentui/react-components";
import {
  ArrowsBidirectionalRegular,
  BotRegular,
  CheckmarkCircle16Filled,
  GavelRegular,
  PersonRegular,
} from "@fluentui/react-icons";
import { controlPlaneApi } from "../services/controlPlaneApi";
import type { AgentMessage } from "../services/controlPlaneApi";

interface AgentDebatePanelProps {
  runId: string;
  accessToken?: string;
}

const ROLE_ICON: Record<string, React.ReactElement> = {
  observer: <PersonRegular fontSize={14} />,
  proposer: <BotRegular fontSize={14} />,
  mediator: <GavelRegular fontSize={14} />,
  executor: <ArrowsBidirectionalRegular fontSize={14} />,
  system: <BotRegular fontSize={14} />,
};

const ROLE_COLOR: Record<
  string,
  "brand" | "warning" | "success" | "informative" | "subtle"
> = {
  observer: "informative",
  proposer: "brand",
  mediator: "warning",
  executor: "success",
  system: "subtle",
};

const HIGHLIGHT_TYPES = new Set([
  "conflict",
  "negotiation",
  "mediation",
  "tradeoff",
]);

const useStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalXS,
  },
  header: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalS,
    paddingBottom: tokens.spacingVerticalXS,
  },
  timeline: {
    display: "flex",
    flexDirection: "column",
    gap: "2px",
    maxHeight: "400px",
    overflow: "auto",
  },
  entry: {
    display: "flex",
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    borderRadius: tokens.borderRadiusSmall,
    borderLeft: `3px solid transparent`,
  },
  entryHighlighted: {
    backgroundColor: tokens.colorBrandBackground2,
    borderLeftColor: tokens.colorBrandForeground1,
  },
  entryMeta: {
    flexShrink: 0,
    minWidth: "120px",
    display: "flex",
    flexDirection: "column",
    gap: "2px",
  },
  entryBody: {
    flex: 1,
    wordBreak: "break-word",
  },
  timestamp: {
    color: tokens.colorNeutralForeground3,
    fontSize: tokens.fontSizeBase100,
  },
  emptyState: {
    color: tokens.colorNeutralForeground3,
    padding: tokens.spacingVerticalM,
    textAlign: "center",
  },
  confidence: {
    marginLeft: "auto",
    flexShrink: 0,
  },
});

function formatTime(iso: string): string {
  try {
    return new Date(iso).toLocaleTimeString(undefined, {
      hour: "2-digit",
      minute: "2-digit",
      second: "2-digit",
    });
  } catch {
    return iso;
  }
}

function tryParsePayloadSummary(payload?: string | null): string {
  if (!payload) return "";
  try {
    const obj = JSON.parse(payload) as Record<string, unknown>;
    return (
      (obj["summary"] as string) ??
      (obj["narrative"] as string) ??
      (obj["message"] as string) ??
      (obj["finding"] as string) ??
      ""
    );
  } catch {
    return payload.slice(0, 200);
  }
}

/**
 * Agent Debate Panel — renders the inter-agent message timeline for an analysis run.
 *
 * Shows all AgentMessage rows persisted during the run, highlighting
 * conflict-detection and mediation messages as "the interesting moments".
 * This is the visible artefact of the governance negotiation process described in US4.
 *
 * Compliance: All React hooks (useState, useEffect) declared at top level per Rules of Hooks (FW-001).
 */
export function AgentDebatePanel({
  runId,
  accessToken,
}: AgentDebatePanelProps) {
  const styles = useStyles();
  // Hooks declared at top level (compliant with React Rules of Hooks)
  const [messages, setMessages] = useState<AgentMessage[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | undefined>();

  useEffect(() => {
    if (!runId) return;
    let cancelled = false;

    async function fetchMessages() {
      const correlationId = crypto.randomUUID();
      setLoading(true);
      setError(undefined);
      try {
        const res = await controlPlaneApi.getAgentMessages(
          runId!,
          accessToken,
          correlationId,
        );
        if (!cancelled) setMessages(res.value ?? []);
      } catch (err: unknown) {
        if (!cancelled)
          setError(
            err instanceof Error
              ? err.message
              : "Failed to load agent messages",
          );
      } finally {
        if (!cancelled) setLoading(false);
      }
    }

    void fetchMessages();
    return () => {
      cancelled = true;
    };
  }, [runId, accessToken]);

  if (loading) {
    return (
      <Card>
        <Spinner size="small" label="Loading agent debate…" />
      </Card>
    );
  }

  if (error) {
    return null; // Silently omit on error — panel is additive
  }

  const interestingMessages = messages.filter(
    (m) =>
      HIGHLIGHT_TYPES.has(m.messageType.toLowerCase()) ||
      m.agentRole === "mediator",
  );

  return (
    <Card role="region" aria-label="Agent debate timeline">
      <div className={styles.root}>
        <div className={styles.header}>
          <GavelRegular fontSize={20} />
          <Text size={300} weight="semibold">
            Agent Debate &amp; Negotiation
          </Text>
          <Badge appearance="tint" color="informative" size="small">
            {messages.length} message{messages.length !== 1 ? "s" : ""}
          </Badge>
          {interestingMessages.length > 0 && (
            <Badge appearance="tint" color="warning" size="small">
              {interestingMessages.length} conflict/mediation
            </Badge>
          )}
          <div
            style={{
              display: "flex",
              alignItems: "center",
              gap: tokens.spacingHorizontalXS,
              marginLeft: "auto",
            }}
          >
            {Array.from(new Set(messages.map((m) => m.agentName))).map(
              (name) => (
                <Tooltip key={name} content={name} relationship="description">
                  <Badge appearance="ghost" size="extra-small">
                    {name.replace(/Agent$/, "")}
                  </Badge>
                </Tooltip>
              ),
            )}
          </div>
        </div>

        <Divider />

        <div className={styles.timeline} aria-label="Message timeline">
          {messages.length === 0 ? (
            <Text size={200} className={styles.emptyState}>
              No agent messages recorded for this run.
            </Text>
          ) : (
            messages.map((m) => {
              const highlighted =
                HIGHLIGHT_TYPES.has(m.messageType.toLowerCase()) ||
                m.agentRole === "mediator";
              const summary = tryParsePayloadSummary(m.payload);
              return (
                <div
                  key={m.id}
                  className={`${styles.entry} ${highlighted ? styles.entryHighlighted : ""}`}
                >
                  <div className={styles.entryMeta}>
                    <div
                      style={{ display: "flex", alignItems: "center", gap: 4 }}
                    >
                      {ROLE_ICON[m.agentRole] ?? <BotRegular fontSize={14} />}
                      <Badge
                        appearance="tint"
                        color={ROLE_COLOR[m.agentRole] ?? "subtle"}
                        size="extra-small"
                      >
                        {m.agentRole}
                      </Badge>
                    </div>
                    <Text size={100} weight="semibold" truncate>
                      {m.agentName.replace(/Agent$/, "")}
                    </Text>
                    <Text size={100} className={styles.timestamp}>
                      {formatTime(m.createdAt)}
                    </Text>
                  </div>

                  <div className={styles.entryBody}>
                    <div
                      style={{
                        display: "flex",
                        alignItems: "center",
                        gap: tokens.spacingHorizontalXS,
                      }}
                    >
                      <Badge
                        appearance="ghost"
                        size="extra-small"
                        color="subtle"
                      >
                        {m.messageType}
                      </Badge>
                      {highlighted && (
                        <CheckmarkCircle16Filled
                          color={tokens.colorStatusSuccessForeground1}
                          aria-label="Resolved"
                        />
                      )}
                    </div>
                    {summary && (
                      <Text
                        size={200}
                        style={{ display: "block", marginTop: 2 }}
                      >
                        {summary}
                      </Text>
                    )}
                  </div>

                  {m.confidence !== undefined && m.confidence !== null && (
                    <Tooltip
                      content={`Confidence: ${(m.confidence * 100).toFixed(0)}%`}
                      relationship="description"
                    >
                      <Badge
                        appearance="tint"
                        color={
                          m.confidence >= 0.7
                            ? "success"
                            : m.confidence >= 0.4
                              ? "warning"
                              : "danger"
                        }
                        size="small"
                        className={styles.confidence}
                      >
                        {(m.confidence * 100).toFixed(0)}%
                      </Badge>
                    </Tooltip>
                  )}
                </div>
              );
            })
          )}
        </div>
      </div>
    </Card>
  );
}
