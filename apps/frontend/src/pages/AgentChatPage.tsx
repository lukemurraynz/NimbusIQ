/**
 * AgentChatPage — full-page AG-UI chat interface.
 *
 * Wraps the AgentChatPanel inside the AzurePageHeader shell, providing
 * consistent breadcrumb navigation and description.  The chat panel fills
 * the remaining height so the conversation always scrolls correctly.
 */

import { useEffect } from "react";
import { makeStyles, tokens, Text } from "@fluentui/react-components";
import { BotSparkle24Regular } from "@fluentui/react-icons";
import { AzurePageHeader } from "../components/AzurePageHeader";
import { AgentChatPanel } from "../components/AgentChatPanel";
import { useAccessToken } from "../auth/useAccessToken";

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
  chatWrapper: {
    flex: 1,
    overflow: "hidden",
    display: "flex",
    flexDirection: "column",
    borderRadius: tokens.borderRadiusMedium,
    boxShadow: tokens.shadow4,
  },
  sideInfo: {
    width: "280px",
    flexShrink: 0,
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalM,
    overflowY: "auto",
  },
  infoCard: {
    backgroundColor: tokens.colorNeutralBackground1,
    border: `1px solid ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    padding: tokens.spacingHorizontalM,
  },
  infoTitle: {
    fontWeight: tokens.fontWeightSemibold,
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground1,
    marginBottom: tokens.spacingVerticalXS,
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalXS,
  },
  infoText: {
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    lineHeight: tokens.lineHeightBase400,
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
});

/**
 * Main chat page — uses AG-UI over SSE to stream real-time AI analysis
 * from the NimbusIQ control plane.
 */
export function AgentChatPage() {
  const styles = useStyles();
  const { accessToken } = useAccessToken();

  useEffect(() => {
    document.title = "NimbusIQ — AI Chat";
  }, []);

  return (
    <div className={styles.page}>
      <AzurePageHeader
        title="AI Assistant"
        subtitle="Interactive, streaming AI analysis of your Azure infrastructure powered by the AG-UI protocol"
        breadcrumbs={
          <>
            <a
              href="/dashboard"
              style={{ color: "inherit", textDecoration: "none" }}
            >
              Home
            </a>
            <span aria-hidden="true"> / </span>
            <span>AI Assistant</span>
          </>
        }
      />

      <div className={styles.body}>
        {/* Main chat area */}
        <div className={styles.chatWrapper}>
          <AgentChatPanel accessToken={accessToken} />
        </div>

        {/* Side info panel */}
        <aside className={styles.sideInfo} aria-label="About the AI assistant">
          <div className={styles.infoCard}>
            <div className={styles.infoTitle}>
              <BotSparkle24Regular aria-hidden="true" />
              About the assistant
            </div>
            <Text className={styles.infoText} as="p">
              The NimbusIQ AI assistant uses live data from your environment to
              answer questions about your Azure infrastructure. Responses are
              streamed in real-time.
            </Text>
          </div>

          <div className={styles.infoCard}>
            <Text className={styles.infoTitle} as="h3">
              What I can help with
            </Text>
            <ul
              className={styles.infoText as string}
              style={{ margin: 0, paddingLeft: "1.2em", fontSize: "inherit" }}
            >
              <li>Service groups, recommendation IDs, and analysis runs</li>
              <li>Configuration drift trends and likely causes</li>
              <li>Governance conflicts and recommended trade-offs</li>
              <li>Best-practice violations and evidence-backed actions</li>
              <li>Navigation into remediation-ready product flows</li>
            </ul>
          </div>

          <div className={styles.infoCard}>
            <Text className={styles.infoTitle} as="h3">
              How it works
            </Text>
            <Text className={styles.infoText} as="p">
              Your question is sent to the NimbusIQ control plane API which
              queries the database, emits visible tool calls so you can see what
              data is being fetched, and streams the natural-language response
              back in real-time. Provider-backed answers are labeled through the
              live capability badges in the chat panel so you can distinguish
              preview-style guidance from executable product flows.
            </Text>
          </div>
        </aside>
      </div>
    </div>
  );
}
