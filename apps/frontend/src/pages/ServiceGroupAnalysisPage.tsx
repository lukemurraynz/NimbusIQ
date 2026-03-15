import { useEffect, useState } from "react";
import { useNavigate } from "react-router-dom";
import {
  Card,
  Spinner,
  Button,
  Input,
  makeStyles,
  tokens,
  Text,
  Badge,
  ProgressBar,
  MessageBar,
  MessageBarBody,
  Divider,
} from "@fluentui/react-components";
import {
  ArrowDownload24Regular,
  ArrowSyncCircle20Regular,
  ArrowRight16Regular,
  CheckmarkCircle16Regular,
  DismissCircle16Regular,
} from "@fluentui/react-icons";
import { useAccessToken } from "../auth/useAccessToken";
import { AzurePageHeader } from "../components/AzurePageHeader";
import { useServiceGroupDiscovery } from "../hooks/useServiceGroupDiscovery";
import { useAnalysisPolling } from "../hooks/useAnalysisPolling";
import { useAgentStream } from "../hooks/useAgentStream";
import { AgentActivityPanel } from "../components/AgentActivityPanel";
import { AgentDebatePanel } from "../components/AgentDebatePanel";
import { ConfidenceDisclosure } from "../components/ConfidenceDisclosure";
import {
  controlPlaneApi,
  type BlastRadiusResponse,
  type ServiceGroup,
} from "../services/controlPlaneApi";

const DIMENSION_META: Record<string, { label: string; description: string }> = {
  completeness: {
    label: "Metadata Completeness",
    description:
      "Weighted resource metadata coverage: tags 30%, region 25%, SKU 25%, kind 20%, with a small managed-service uplift.",
  },
  cost_efficiency: {
    label: "Cost Efficiency",
    description:
      "Fraction of trackable billable resource types (VMs, storage, databases)",
  },
  availability: {
    label: "Availability",
    description:
      "Fraction of resources using high-availability SKUs (not Basic/Free/Developer)",
  },
  security: {
    label: "Security",
    description:
      "Security posture baseline — 50% until Microsoft Defender data is integrated",
  },
};

const useStyles = makeStyles({
  container: {
    display: "flex",
    flexDirection: "column",
    height: "100%",
  },
  content: {
    flex: 1,
    overflow: "auto",
    padding: tokens.spacingHorizontalXXL,
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalL,
  },
  card: {
    padding: tokens.spacingHorizontalL,
  },
  cardActions: {
    display: "flex",
    gap: tokens.spacingHorizontalM,
    marginTop: tokens.spacingVerticalM,
  },
  scorePanel: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalM,
    padding: tokens.spacingHorizontalL,
    backgroundColor: tokens.colorNeutralBackground2,
    borderRadius: tokens.borderRadiusMedium,
    marginTop: tokens.spacingVerticalM,
  },
  scorePanelHeader: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "center",
    flexWrap: "wrap",
    gap: tokens.spacingHorizontalM,
  },
  scorePanelTitleGroup: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalS,
  },
  scorePanelFooter: {
    display: "flex",
    justifyContent: "flex-end",
    paddingTop: tokens.spacingVerticalXS,
  },
  dimensionRow: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalXS,
  },
  dimensionHeader: {
    display: "flex",
    justifyContent: "space-between",
    alignItems: "baseline",
  },
  noScopesNotice: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalS,
    color: tokens.colorNeutralForeground3,
  },
  emptyState: {
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    justifyContent: "center",
    padding: tokens.spacingHorizontalXXL,
    gap: tokens.spacingVerticalL,
    minHeight: "300px",
    textAlign: "center",
  },
  loadingContainer: {
    display: "flex",
    justifyContent: "center",
    alignItems: "center",
    minHeight: "300px",
  },
  blastRadiusCard: {
    marginTop: tokens.spacingVerticalL,
    padding: tokens.spacingHorizontalL,
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalM,
  },
  blastRadiusControls: {
    display: "flex",
    gap: tokens.spacingHorizontalS,
    flexWrap: "wrap",
    alignItems: "center",
  },
  blastRadiusSummary: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fit, minmax(180px, 1fr))",
    gap: tokens.spacingHorizontalS,
  },
  blastRadiusList: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalXS,
  },
});

/**
 * T026: Service Group selector with Azure Service Group discovery support
 */
export function ServiceGroupAnalysisPage() {
  const styles = useStyles();
  const { accessToken } = useAccessToken();
  const navigate = useNavigate();

  useEffect(() => {
    document.title = "NimbusIQ — Service Groups";
  }, []);

  // Hook: Discovery (list + Azure import)
  const {
    serviceGroups,
    loading,
    discovering,
    discoverResult,
    error: discoveryError,
    loadServiceGroups,
    discoverFromAzure,
  } = useServiceGroupDiscovery(accessToken);

  // Hook: Analysis polling + scoring
  const {
    analysisScore,
    analyzingGroupId,
    activeRunId,
    completedRunId,
    error: analysisError,
    startAnalysis,
  } = useAnalysisPolling(accessToken);

  // Merge errors (discovery + analysis)
  const error = discoveryError || analysisError;

  // Track selected group for result display and SignalR run ID
  const [selectedGroup, setSelectedGroup] = useState<ServiceGroup | null>(null);
  const [blastRadiusInput, setBlastRadiusInput] = useState("");
  const [blastRadiusLoading, setBlastRadiusLoading] = useState(false);
  const [blastRadiusError, setBlastRadiusError] = useState<string | null>(null);
  const [blastRadius, setBlastRadius] = useState<BlastRadiusResponse | null>(
    null,
  );

  // Real-time SignalR stream (fires while a run is in progress)
  const agentStream = useAgentStream(activeRunId, accessToken);

  const handleStartAnalysis = async (group: ServiceGroup) => {
    setSelectedGroup(group);
    await startAnalysis(group.id);
  };

  const loadBlastRadius = async (serviceGroupId: string) => {
    const resourceIds = blastRadiusInput
      .split(",")
      .map((id) => id.trim())
      .filter((id) => id.length > 0);

    if (resourceIds.length === 0) {
      setBlastRadiusError("Enter one or more Azure resource IDs.");
      setBlastRadius(null);
      return;
    }

    setBlastRadiusLoading(true);
    setBlastRadiusError(null);
    try {
      const result = await controlPlaneApi.getBlastRadius(
        serviceGroupId,
        { resourceIds },
        accessToken ?? undefined,
        crypto.randomUUID(),
      );
      setBlastRadius(result);
    } catch (err) {
      setBlastRadiusError(
        err instanceof Error ? err.message : "Failed to load blast radius",
      );
      setBlastRadius(null);
    } finally {
      setBlastRadiusLoading(false);
    }
  };

  return (
    <div className={styles.container}>
      <AzurePageHeader
        title="Service Groups"
        subtitle="Manage and analyze your Azure Service Groups"
        commands={
          <>
            <Button
              appearance="subtle"
              icon={<ArrowSyncCircle20Regular />}
              onClick={loadServiceGroups}
              disabled={loading || discovering}
            >
              Refresh
            </Button>
            <Button
              appearance="primary"
              icon={<ArrowDownload24Regular />}
              onClick={discoverFromAzure}
              disabled={discovering || loading}
            >
              {discovering ? "Discovering…" : "Discover from Azure"}
            </Button>
          </>
        }
      />

      <div className={styles.content}>
        {discovering && (
          <div className={styles.loadingContainer}>
            <Spinner label="Discovering Azure Service Groups…" />
          </div>
        )}

        {discoverResult && (
          <MessageBar intent="success">
            <MessageBarBody>
              Discovery complete — found {discoverResult.discovered} service
              group(s); {discoverResult.created} created,{" "}
              {discoverResult.updated} updated.
            </MessageBarBody>
          </MessageBar>
        )}

        {error && (
          <MessageBar intent="error">
            <MessageBarBody>{error}</MessageBarBody>
          </MessageBar>
        )}

        {loading && !discovering && (
          <div className={styles.loadingContainer}>
            <Spinner label="Loading service groups…" />
          </div>
        )}

        {!loading && !discovering && serviceGroups.length === 0 && !error && (
          <div className={styles.emptyState}>
            <Text size={600} weight="semibold">
              No service groups found
            </Text>
            <Text
              size={400}
              style={{
                color: tokens.colorNeutralForeground3,
                maxWidth: "480px",
              }}
            >
              Click <strong>Discover from Azure</strong> to import your Azure
              Service Groups automatically, or create a service group manually
              via the API.
            </Text>
            <Button
              appearance="primary"
              icon={<ArrowDownload24Regular />}
              onClick={discoverFromAzure}
            >
              Discover from Azure
            </Button>
          </div>
        )}

        {!loading &&
          !discovering &&
          serviceGroups.map((group) => (
            <Card key={group.id} className={styles.card}>
              <Text size={500} weight="semibold">
                {group.name}
              </Text>
              {group.description && <Text>{group.description}</Text>}
              <Text
                size={300}
                style={{ color: tokens.colorNeutralForeground3 }}
              >
                Scopes: {group.scopeCount}
              </Text>

              <div className={styles.cardActions}>
                <Button
                  appearance="primary"
                  disabled={!!analyzingGroupId}
                  onClick={() => handleStartAnalysis(group)}
                >
                  {analyzingGroupId === group.id
                    ? "Analysing…"
                    : "Start Analysis"}
                </Button>
                {analyzingGroupId === group.id && (
                  <Spinner size="tiny" label="Running analysis…" />
                )}
                <Button
                  appearance="secondary"
                  onClick={() => navigate(`/service-groups/${group.id}/health`)}
                >
                  View Health
                </Button>
              </div>

              {/* Live agent activity panel (while running) */}
              {selectedGroup?.id === group.id &&
                analyzingGroupId === group.id && (
                  <AgentActivityPanel
                    agents={agentStream.agents}
                    findings={agentStream.findings}
                    runCompleted={agentStream.runCompleted}
                    overallScore={agentStream.overallScore}
                  />
                )}

              {selectedGroup?.id === group.id && analysisScore && (
                <div className={styles.scorePanel}>
                  {/* Header: overall badge + resource count */}
                  <div className={styles.scorePanelHeader}>
                    <div className={styles.scorePanelTitleGroup}>
                      <Badge
                        appearance="filled"
                        color={
                          analysisScore.level === "high"
                            ? "success"
                            : analysisScore.level === "medium"
                              ? "warning"
                              : "danger"
                        }
                      >
                        {analysisScore.level}
                      </Badge>
                      <Text weight="semibold">
                        Overall Score: {(analysisScore.value * 100).toFixed(0)}%
                      </Text>
                    </div>
                    {analysisScore.resourceCount > 0 ? (
                      <Text
                        size={300}
                        style={{ color: tokens.colorNeutralForeground3 }}
                      >
                        <CheckmarkCircle16Regular
                          style={{
                            verticalAlign: "middle",
                            marginRight: "4px",
                            color: tokens.colorPaletteGreenForeground1,
                          }}
                        />
                        {analysisScore.resourceCount} resource
                        {analysisScore.resourceCount !== 1 ? "s" : ""} analysed
                      </Text>
                    ) : (
                      <div className={styles.noScopesNotice}>
                        <DismissCircle16Regular />
                        <Text size={300}>
                          No Azure resources found. Verify the managed identity
                          has Reader access to your subscriptions, or add
                          explicit subscription scopes to this service group.
                        </Text>
                      </div>
                    )}
                  </div>

                  {/* Responsible AI confidence disclosure */}
                  <ConfidenceDisclosure
                    confidenceScore={
                      analysisScore.confidence ?? analysisScore.value
                    }
                    confidenceLevel={analysisScore.level}
                    degradationFactors={analysisScore.degradationFactors}
                  />

                  {/* Degradation warnings (kept for legacy compat) */}
                  {analysisScore.degradationFactors.length > 0 && (
                    <MessageBar intent="warning">
                      <MessageBarBody>
                        {analysisScore.degradationFactors.join(" · ")}
                      </MessageBarBody>
                    </MessageBar>
                  )}

                  <Divider />

                  {/* Per-dimension breakdown */}
                  {Object.entries(DIMENSION_META).map(([key, meta]) => {
                    const score = analysisScore.dimensions[key] ?? 0;
                    const color =
                      score >= 0.7
                        ? "success"
                        : score >= 0.4
                          ? "warning"
                          : "error";
                    return (
                      <div key={key} className={styles.dimensionRow}>
                        <div className={styles.dimensionHeader}>
                          <Text size={300} weight="semibold">
                            {meta.label}
                          </Text>
                          <Text
                            size={300}
                            style={{ color: tokens.colorNeutralForeground3 }}
                          >
                            {(score * 100).toFixed(0)}%
                          </Text>
                        </div>
                        <ProgressBar
                          value={score}
                          color={color}
                          thickness="medium"
                        />
                        <Text
                          size={200}
                          style={{ color: tokens.colorNeutralForeground3 }}
                        >
                          {meta.description}
                        </Text>
                      </div>
                    );
                  })}

                  {/* Link to drift timeline */}
                  <div className={styles.scorePanelFooter}>
                    <Button
                      appearance="subtle"
                      size="small"
                      icon={<ArrowRight16Regular />}
                      iconPosition="after"
                      onClick={() => navigate("/drift")}
                    >
                      View drift history
                    </Button>
                  </div>
                </div>
              )}

              {selectedGroup?.id === group.id && analysisScore && (
                <Card className={styles.blastRadiusCard}>
                  <Text size={400} weight="semibold">
                    Blast Radius Analysis
                  </Text>
                  <Text size={200}>
                    Enter one or more Azure resource IDs (comma-separated) to
                    identify impacted applications, identities, and related
                    recommendations.
                  </Text>

                  <div className={styles.blastRadiusControls}>
                    <Input
                      value={blastRadiusInput}
                      onChange={(_, data) => setBlastRadiusInput(data.value)}
                      placeholder="/subscriptions/.../resourceGroups/.../providers/..."
                      style={{ minWidth: "420px", flex: 1 }}
                    />
                    <Button
                      appearance="primary"
                      onClick={() => loadBlastRadius(group.id)}
                      disabled={blastRadiusLoading}
                    >
                      {blastRadiusLoading
                        ? "Analysing..."
                        : "Analyse Blast Radius"}
                    </Button>
                  </div>

                  {blastRadiusError && (
                    <MessageBar intent="error">
                      <MessageBarBody>{blastRadiusError}</MessageBarBody>
                    </MessageBar>
                  )}

                  {blastRadius && (
                    <>
                      <div className={styles.blastRadiusSummary}>
                        <Card>
                          <Text size={500} weight="semibold">
                            {blastRadius.resourceCount}
                          </Text>
                          <Text size={200}>Affected resources</Text>
                        </Card>
                        <Card>
                          <Text size={500} weight="semibold">
                            {blastRadius.identityCount}
                          </Text>
                          <Text size={200}>Affected identities</Text>
                        </Card>
                        <Card>
                          <Text size={500} weight="semibold">
                            {blastRadius.sharedRecommendations.length}
                          </Text>
                          <Text size={200}>Shared recommendations</Text>
                        </Card>
                      </div>

                      <div className={styles.blastRadiusList}>
                        {blastRadius.affectedResources
                          .slice(0, 5)
                          .map((item) => (
                            <Text key={`res-${item.resourceId}`} size={200}>
                              {item.name} · {item.impactType}
                            </Text>
                          ))}
                        {blastRadius.affectedIdentities
                          .slice(0, 3)
                          .map((item) => (
                            <Text key={`id-${item.resourceId}`} size={200}>
                              {item.name} · IdentityAccess
                            </Text>
                          ))}
                      </div>
                    </>
                  )}
                </Card>
              )}

              {/* Agent Debate Panel: shown after analysis completes */}
              {selectedGroup?.id === group.id &&
                analysisScore &&
                completedRunId && (
                  <AgentDebatePanel
                    runId={completedRunId}
                    accessToken={accessToken}
                  />
                )}
            </Card>
          ))}
      </div>
    </div>
  );
}
