/**
 * AgentChatPanel — Azure Portal-style chat interface for the AG-UI agent.
 *
 * Renders a full-height chat UI with:
 *   • Message bubbles (user right / assistant left) with markdown-style bold support
 *   • Tool-call cards showing the agent's "thinking" (collapsible)
 *   • Streaming text animation via the AG-UI protocol
 *   • Infrastructure state summary chip strip
 *   • Accessible keyboard navigation (Enter to send, Shift+Enter for newline)
 *
 * Uses Fluent UI v9 design tokens to stay on-brand with Azure Portal.
 */

import { useCallback, useEffect, useMemo, useRef, useState } from "react";
import { useNavigate } from "react-router-dom";
import {
  makeStyles,
  tokens,
  Text,
  Button,
  Textarea,
  Spinner,
  Badge,
  Tooltip,
} from "@fluentui/react-components";
import {
  Send24Regular,
  Delete24Regular,
  ArrowSync24Regular,
  ChevronDown16Regular,
  ChevronRight16Regular,
  BotSparkle24Regular,
  Wrench16Regular,
  Info16Regular,
} from "@fluentui/react-icons";
import { useAgentChat } from "../hooks/useAgentChat";
import type { ChatMessage, ToolCall } from "../hooks/useAgentChat";

// ─── Styles ───────────────────────────────────────────────────────────────────

const useStyles = makeStyles({
  root: {
    display: "flex",
    flexDirection: "column",
    height: "100%",
    backgroundColor: tokens.colorNeutralBackground1,
    borderRadius: tokens.borderRadiusMedium,
    overflow: "hidden",
  },

  // Top info banner
  stateBanner: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalS,
    flexWrap: "wrap",
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}`,
    backgroundColor: tokens.colorNeutralBackground2,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    minHeight: "32px",
  },
  stateBannerLabel: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
  },

  // Message list
  messageList: {
    flex: 1,
    overflowY: "auto",
    padding: tokens.spacingHorizontalM,
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalM,
  },

  // Welcome state
  welcomeArea: {
    flex: 1,
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    justifyContent: "center",
    gap: tokens.spacingVerticalL,
    color: tokens.colorNeutralForeground3,
    padding: tokens.spacingHorizontalXXL,
    textAlign: "center",
  },
  welcomeIcon: {
    fontSize: "40px",
    color: tokens.colorBrandForeground1,
    opacity: 0.7,
  },
  welcomeTitle: {
    fontSize: tokens.fontSizeBase500,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },
  welcomeSubtitle: {
    fontSize: tokens.fontSizeBase300,
    lineHeight: tokens.lineHeightBase400,
  },
  suggestionList: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalXS,
    marginTop: tokens.spacingVerticalS,
    width: "100%",
    maxWidth: "380px",
  },
  suggestionBtn: {
    textAlign: "left",
    justifyContent: "flex-start",
    fontSize: tokens.fontSizeBase200,
    height: "auto",
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    "&:hover": {
      backgroundColor: tokens.colorNeutralBackground2Hover,
    },
  },

  // Message row wrapper
  messageRow: {
    display: "flex",
    flexDirection: "column",
    gap: "2px",
  },
  messageRowUser: {
    alignItems: "flex-end",
  },
  messageRowAssistant: {
    alignItems: "flex-start",
  },

  // Bubble
  bubble: {
    maxWidth: "80%",
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    borderRadius: tokens.borderRadiusMedium,
    lineHeight: tokens.lineHeightBase400,
    fontSize: tokens.fontSizeBase300,
    whiteSpace: "pre-wrap",
    wordBreak: "break-word",
  },
  bubbleUser: {
    backgroundColor: tokens.colorBrandBackground,
    color: tokens.colorNeutralForegroundInverted,
    borderBottomRightRadius: tokens.borderRadiusSmall,
  },
  bubbleAssistant: {
    backgroundColor: tokens.colorNeutralBackground2,
    color: tokens.colorNeutralForeground1,
    borderBottomLeftRadius: tokens.borderRadiusSmall,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
  },

  // Streaming cursor
  cursor: {
    display: "inline-block",
    width: "8px",
    height: "14px",
    backgroundColor: tokens.colorBrandForeground1,
    borderRadius: "2px",
    marginLeft: "2px",
    verticalAlign: "middle",
    animationName: {
      "0%, 100%": { opacity: 1 },
      "50%": { opacity: 0 },
    },
    animationDuration: "900ms",
    animationIterationCount: "infinite",
  },

  // Tool call cards
  toolCard: {
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    overflow: "hidden",
    maxWidth: "80%",
  },
  toolCardHeader: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalXS,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    backgroundColor: tokens.colorNeutralBackground3,
    cursor: "pointer",
    userSelect: "none",
    "&:hover": {
      backgroundColor: tokens.colorNeutralBackground3Hover,
    },
  },
  toolCardName: {
    flex: 1,
    fontSize: tokens.fontSizeBase200,
    fontFamily: tokens.fontFamilyMonospace,
    color: tokens.colorNeutralForeground2,
  },
  toolCardBody: {
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    backgroundColor: tokens.colorNeutralBackground1,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    fontSize: tokens.fontSizeBase100,
    fontFamily: tokens.fontFamilyMonospace,
    color: tokens.colorNeutralForeground3,
    overflowX: "auto",
    maxHeight: "120px",
    overflowY: "auto",
    whiteSpace: "pre-wrap",
    wordBreak: "break-all",
  },

  // Error banner
  errorBanner: {
    margin: `0 ${tokens.spacingHorizontalM} ${tokens.spacingVerticalXS}`,
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalM}`,
    backgroundColor: tokens.colorStatusDangerBackground1,
    border: `1px solid ${tokens.colorStatusDangerBorderActive}`,
    borderRadius: tokens.borderRadiusMedium,
    color: tokens.colorStatusDangerForeground1,
    fontSize: tokens.fontSizeBase200,
  },

  // Input row
  inputRow: {
    display: "flex",
    alignItems: "flex-end",
    gap: tokens.spacingHorizontalS,
    padding: tokens.spacingHorizontalM,
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  inputWrapper: {
    flex: 1,
    position: "relative",
  },
  textarea: {
    width: "100%",
    resize: "none",
    fontFamily: tokens.fontFamilyBase,
    fontSize: tokens.fontSizeBase300,
    minHeight: "40px",
    maxHeight: "120px",
  },
  sendBtn: {
    flexShrink: 0,
    alignSelf: "flex-end",
  },
  resetBtn: {
    flexShrink: 0,
    alignSelf: "flex-end",
  },
  quickActionRow: {
    display: "flex",
    gap: tokens.spacingHorizontalS,
    flexWrap: "wrap",
    padding: `0 ${tokens.spacingHorizontalM} ${tokens.spacingVerticalS}`,
  },
});

// ─── Sub-components ───────────────────────────────────────────────────────────

function ToolCallCard({ tool }: { tool: ToolCall }) {
  const styles = useStyles();
  const [expanded, setExpanded] = useState(false);

  return (
    <div
      className={styles.toolCard}
      role="region"
      aria-label={`Tool call: ${tool.name}`}
    >
      <div
        className={styles.toolCardHeader}
        onClick={() => setExpanded((v) => !v)}
        onKeyDown={(e) =>
          (e.key === "Enter" || e.key === " ") && setExpanded((v) => !v)
        }
        role="button"
        tabIndex={0}
        aria-expanded={expanded}
      >
        <Wrench16Regular aria-hidden="true" />
        {tool.running ? (
          <Spinner size="extra-tiny" aria-label="Tool running" />
        ) : null}
        <span className={styles.toolCardName}>{tool.name}</span>
        {expanded ? (
          <ChevronDown16Regular aria-hidden="true" />
        ) : (
          <ChevronRight16Regular aria-hidden="true" />
        )}
      </div>
      {expanded && (
        <div className={styles.toolCardBody}>
          {tool.args && (
            <div>
              <strong>Args:</strong> {tool.args}
            </div>
          )}
          {tool.output && (
            <div>
              <strong>Output:</strong> {tool.output}
            </div>
          )}
        </div>
      )}
    </div>
  );
}

/** Render a chat message, converting **bold** markdown to <strong> elements. */
function MessageBubble({ message }: { message: ChatMessage }) {
  const styles = useStyles();

  const isUser = message.role === "user";

  // Simple **bold** markdown renderer
  const parts = message.content.split(/(\*\*[^*]+\*\*)/g);
  const rendered = parts.map((part, i) => {
    if (part.startsWith("**") && part.endsWith("**")) {
      return <strong key={i}>{part.slice(2, -2)}</strong>;
    }
    return <span key={i}>{part}</span>;
  });

  return (
    <div
      className={`${styles.bubble} ${isUser ? styles.bubbleUser : styles.bubbleAssistant}`}
      role="article"
      aria-label={isUser ? "You" : "NimbusIQ assistant"}
    >
      {rendered}
      {message.streaming && (
        <span className={styles.cursor} aria-hidden="true" />
      )}
    </div>
  );
}

// ─── Main component ───────────────────────────────────────────────────────────

const SUGGESTIONS = [
  "💡 What should I improve in my infrastructure?",
  "📉 Show me recent drift analysis",
  "🛡️ Are there any compliance violations?",
  "📦 What Azure resources have been discovered?",
  "📊 Give me a health summary",
];

interface AgentChatPanelProps {
  accessToken?: string;
}

/**
 * Full chat panel implementing the AG-UI client protocol.
 * Streams agent responses from POST /api/v1/chat/agent.
 */
export function AgentChatPanel({ accessToken }: AgentChatPanelProps) {
  const styles = useStyles();
  const navigate = useNavigate();
  const {
    state,
    sendMessage,
    retryLast,
    canRetryLast,
    clearError,
    reset,
    restoredFromSession,
  } = useAgentChat();

  const [input, setInput] = useState("");
  const listEndRef = useRef<HTMLDivElement>(null);

  // Auto-scroll to bottom on new messages
  useEffect(() => {
    listEndRef.current?.scrollIntoView({ behavior: "smooth" });
  }, [state.messages, state.toolCalls]);

  const handleSend = useCallback(async () => {
    const text = input.trim();
    if (!text || state.running) return;
    setInput("");
    await sendMessage(text, { accessToken });
  }, [input, state.running, sendMessage, accessToken]);

  const handleKeyDown = useCallback(
    (e: React.KeyboardEvent<HTMLTextAreaElement>) => {
      if (e.key === "Enter" && !e.shiftKey) {
        e.preventDefault();
        void handleSend();
      }
    },
    [handleSend],
  );

  const handleSuggestion = useCallback(
    (text: string) => {
      void sendMessage(text.replace(/^[^\s]+ /, ""), { accessToken });
    },
    [sendMessage, accessToken],
  );

  const { infraState } = state;
  const hasState = Boolean(infraState.serviceGroupCount !== undefined);
  const contextualSuggestions = useMemo(() => {
    const suggestions = [...SUGGESTIONS];

    if (infraState.topRecommendation?.title) {
      suggestions.unshift(
        `🧭 Why is recommendation ${infraState.topRecommendation.id} the top priority?`,
      );
    }

    if (infraState.latestDrift?.serviceGroupId) {
      suggestions.unshift("📉 What changed in the latest drift event?");
    }

    if (infraState.governanceConflict?.resourceId) {
      suggestions.unshift("⚖️ Explain the current governance conflict");
    }

    return suggestions.slice(0, 5);
  }, [
    infraState.governanceConflict?.resourceId,
    infraState.latestDrift?.serviceGroupId,
    infraState.topRecommendation?.id,
    infraState.topRecommendation?.title,
  ]);

  // Determine current tool calls to show (only running or from the last turn)
  const visibleTools = state.toolCalls.slice(-4);
  const hasRunningTool = visibleTools.some((tc) => tc.running);
  const runningStatusText = hasRunningTool
    ? "Collecting live context"
    : "Composing response";

  return (
    <div
      className={styles.root}
      role="region"
      aria-label="NimbusIQ AI assistant"
    >
      {/* Infrastructure context chips */}
      {hasState && (
        <div
          className={styles.stateBanner}
          aria-live="polite"
          aria-label="Infrastructure context"
        >
          <Tooltip
            content="Live data from your NimbusIQ environment"
            relationship="label"
          >
            <Info16Regular
              aria-hidden="true"
              style={{ color: tokens.colorNeutralForeground3 }}
            />
          </Tooltip>
          <Text className={styles.stateBannerLabel}>Context:</Text>
          {infraState.serviceGroupCount !== undefined && (
            <Badge appearance="outline" size="small">
              {infraState.serviceGroupCount} service groups
            </Badge>
          )}
          {infraState.findingCount !== undefined &&
            infraState.findingCount > 0 && (
              <Badge appearance="outline" color="warning" size="small">
                {infraState.findingCount} findings
              </Badge>
            )}
          {infraState.recentRunStatuses?.map((s, i) => (
            <Badge
              key={i}
              appearance="outline"
              color={
                s === "completed"
                  ? "success"
                  : s === "failed"
                    ? "danger"
                    : "informative"
              }
              size="small"
            >
              {s}
            </Badge>
          ))}
          {state.running && (
            <Badge appearance="filled" color="informative" size="small">
              {runningStatusText}
            </Badge>
          )}
          {infraState.capabilityModes?.remediation && (
            <Badge appearance="outline" color="brand" size="small">
              Remediation: {infraState.capabilityModes.remediation}
            </Badge>
          )}
          {infraState.capabilityModes?.drift && (
            <Badge appearance="outline" color="informative" size="small">
              Drift: {infraState.capabilityModes.drift}
            </Badge>
          )}
          {!state.running && restoredFromSession && (
            <Badge appearance="outline" color="informative" size="small">
              Session restored
            </Badge>
          )}
        </div>
      )}

      {/* Message area */}
      <div
        className={styles.messageList}
        role="log"
        aria-live="polite"
        aria-label="Conversation"
      >
        {state.messages.length === 0 && !state.running ? (
          /* Welcome / suggestion screen */
          <div className={styles.welcomeArea}>
            <BotSparkle24Regular
              className={styles.welcomeIcon}
              aria-hidden="true"
            />
            <div>
              <Text className={styles.welcomeTitle} as="h2">
                NimbusIQ AI Assistant
              </Text>
              <Text as="p" className={styles.welcomeSubtitle}>
                Ask me anything about your Azure infrastructure. I can analyse
                recommendations, drift trends, compliance violations, and more —
                with live data from your environment.
              </Text>
            </div>
            <div
              className={styles.suggestionList}
              role="list"
              aria-label="Suggested questions"
            >
              {contextualSuggestions.map((s) => (
                <Button
                  key={s}
                  appearance="subtle"
                  className={styles.suggestionBtn}
                  onClick={() => handleSuggestion(s)}
                  role="listitem"
                  aria-label={`Ask: ${s}`}
                >
                  {s}
                </Button>
              ))}
            </div>
          </div>
        ) : (
          <>
            {/* Render messages, interleaved with tool calls */}
            {state.messages.map((msg) => (
              <div
                key={msg.id}
                className={`${styles.messageRow} ${msg.role === "user" ? styles.messageRowUser : styles.messageRowAssistant}`}
              >
                {msg.role === "assistant" && (
                  <Text
                    size={100}
                    style={{
                      color: tokens.colorNeutralForeground3,
                      marginLeft: "4px",
                    }}
                  >
                    NimbusIQ
                  </Text>
                )}
                <MessageBubble message={msg} />
              </div>
            ))}

            {/* Tool call cards (shown near bottom) */}
            {visibleTools.length > 0 && (
              <div
                className={styles.messageRowAssistant}
                style={{
                  display: "flex",
                  flexDirection: "column",
                  gap: tokens.spacingVerticalXS,
                }}
                aria-live="polite"
                aria-label="Agent tool calls"
              >
                {visibleTools.map((tc) => (
                  <ToolCallCard key={tc.id} tool={tc} />
                ))}
              </div>
            )}

            {/* Running spinner when waiting for first token */}
            {state.running && state.messages.at(-1)?.role === "user" && (
              <div
                className={styles.messageRowAssistant}
                style={{ display: "flex" }}
              >
                <div
                  className={`${styles.bubble} ${styles.bubbleAssistant}`}
                  style={{
                    display: "flex",
                    gap: tokens.spacingHorizontalXS,
                    alignItems: "center",
                  }}
                >
                  <Spinner size="extra-tiny" aria-label="Agent thinking" />
                  <Text
                    size={200}
                    style={{ color: tokens.colorNeutralForeground3 }}
                  >
                    Analysing your infrastructure…
                  </Text>
                </div>
              </div>
            )}
          </>
        )}
        <div ref={listEndRef} />
      </div>

      {/* Error */}
      {state.error && (
        <div className={styles.errorBanner} role="alert" aria-live="assertive">
          <div style={{ display: "flex", flexDirection: "column", gap: "8px" }}>
            <Text size={200}>⚠️ {state.error}</Text>
            <div style={{ display: "flex", gap: "8px", flexWrap: "wrap" }}>
              <Button
                appearance="secondary"
                size="small"
                onClick={() => void retryLast({ accessToken })}
                disabled={!canRetryLast}
              >
                Retry last question
              </Button>
              <Button appearance="subtle" size="small" onClick={clearError}>
                Dismiss
              </Button>
            </div>
          </div>
        </div>
      )}

      {(infraState.topRecommendation ||
        infraState.latestDrift ||
        infraState.governanceConflict) && (
        <div className={styles.quickActionRow}>
          {infraState.topRecommendation && (
            <Button
              appearance="secondary"
              size="small"
              onClick={() =>
                navigate(`/recommendations/${infraState.topRecommendation?.id}`)
              }
            >
              Open recommendation
            </Button>
          )}
          {infraState.topRecommendation && (
            <Button
              appearance="secondary"
              size="small"
              onClick={() =>
                void sendMessage(
                  `Generate a remediation plan for recommendation ${infraState.topRecommendation?.id}`,
                  { accessToken },
                )
              }
              disabled={state.running}
            >
              Generate plan
            </Button>
          )}
          {infraState.latestDrift && (
            <Button
              appearance="secondary"
              size="small"
              onClick={() => navigate("/drift")}
            >
              Navigate to drift
            </Button>
          )}
          {infraState.governanceConflict && (
            <Button
              appearance="secondary"
              size="small"
              onClick={() => navigate("/governance/conflicts")}
            >
              Inspect governance conflict
            </Button>
          )}
          {infraState.topRecommendation && (
            <Button
              appearance="secondary"
              size="small"
              onClick={() =>
                navigate(`/recommendations/${infraState.topRecommendation?.id}`)
              }
            >
              Create remediation artifact
            </Button>
          )}
        </div>
      )}

      {/* Input row */}
      <div className={styles.inputRow}>
        <div className={styles.inputWrapper}>
          <Textarea
            className={styles.textarea}
            placeholder="Ask about your Azure infrastructure…"
            value={input}
            onChange={(_, d) => setInput(d.value)}
            onKeyDown={handleKeyDown}
            disabled={state.running}
            aria-label="Chat input"
            resize="none"
          />
        </div>

        <Button
          className={styles.sendBtn}
          appearance="primary"
          icon={<Send24Regular aria-hidden="true" />}
          onClick={() => void handleSend()}
          disabled={!input.trim() || state.running}
          aria-label="Send message"
        >
          Send
        </Button>

        <Tooltip content="New conversation" relationship="label">
          <Button
            className={styles.resetBtn}
            appearance="subtle"
            icon={
              state.running ? (
                <ArrowSync24Regular aria-hidden="true" />
              ) : (
                <Delete24Regular aria-hidden="true" />
              )
            }
            onClick={reset}
            aria-label="Reset conversation"
          />
        </Tooltip>
      </div>
    </div>
  );
}
