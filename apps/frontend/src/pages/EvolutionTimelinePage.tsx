import { useEffect, useState, useCallback } from "react";
import {
  Card,
  Text,
  makeStyles,
  tokens,
  Spinner,
  Badge,
  Button,
  Tooltip,
} from "@fluentui/react-components";
import {
  ArrowSyncCircle20Regular,
  ErrorCircle20Regular,
} from "@fluentui/react-icons";
import { useAccessToken } from "../auth/useAccessToken";
import { useNotify } from "../components/useNotify";
import {
  controlPlaneApi,
  type TimelineEvent,
} from "../services/controlPlaneApi";
import { log } from "../telemetry/logger";

interface ProjectedEvent {
  eventType: string;
  projectedDate: string;
  description: string;
  confidence: number;
  impact: string;
  rationale: string;
}

const useStyles = makeStyles({
  container: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalL,
    padding: tokens.spacingHorizontalXL,
  },
  card: {
    padding: tokens.spacingHorizontalL,
  },
  section: {
    marginTop: tokens.spacingVerticalL,
  },
  eventCard: {
    padding: tokens.spacingHorizontalM,
    marginBottom: tokens.spacingVerticalM,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    borderRadius: tokens.borderRadiusMedium,
  },
  timeline: {
    position: "relative" as const,
    paddingLeft: "40px",
  },
  timelineItem: {
    position: "relative" as const,
    paddingBottom: tokens.spacingVerticalL,
    paddingLeft: tokens.spacingHorizontalL,
    borderLeft: `2px solid ${tokens.colorNeutralStroke1}`,
    "&:last-child": {
      borderLeft: "none",
    },
  },
  timelineDot: {
    position: "absolute" as const,
    left: "-13px",
    top: "2px",
    width: "24px",
    height: "24px",
    borderRadius: "50%",
    display: "flex",
    alignItems: "center",
    justifyContent: "center",
    fontSize: "12px",
    backgroundColor: tokens.colorNeutralBackground1,
    border: `2px solid ${tokens.colorBrandStroke1}`,
    zIndex: 1,
  },
  timelineContent: {
    padding: tokens.spacingHorizontalM,
    border: `1px solid ${tokens.colorNeutralStroke1}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground1,
  },
  futureContent: {
    padding: tokens.spacingHorizontalM,
    border: `1px dashed ${tokens.colorNeutralStroke2}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground3,
    opacity: 0.85,
  },
  nowMarker: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalS,
    padding: `${tokens.spacingVerticalS} ${tokens.spacingHorizontalM}`,
    backgroundColor: tokens.colorBrandBackground2,
    borderRadius: tokens.borderRadiusMedium,
    marginBottom: tokens.spacingVerticalM,
    marginLeft: "-13px",
  },
});

const categoryColorMap: Record<
  string,
  "informative" | "brand" | "danger" | "warning" | "success" | "important"
> = {
  analysis: "brand",
  drift: "danger",
  recommendation: "warning",
  deployment: "success",
  governance: "important",
  score_change: "brand",
  drift_detected: "danger",
  recommendation_generated: "warning",
};

const categoryIconMap: Record<string, string> = {
  analysis: "🔍",
  drift: "📈",
  recommendation: "💡",
  deployment: "🚀",
  governance: "⚖️",
  score_change: "📊",
  drift_detected: "⚠️",
  recommendation_generated: "🎯",
};

/**
 * T052: Timeline UI (past/present/future)
 */
export function EvolutionTimelinePage() {
  const styles = useStyles();
  const { accessToken } = useAccessToken();
  const notify = useNotify();
  const [historicalEvents, setHistoricalEvents] = useState<TimelineEvent[]>([]);
  const [projectedEvents, setProjectedEvents] = useState<ProjectedEvent[]>([]);
  const [loading, setLoading] = useState(true);
  const [error, setError] = useState<string | undefined>(undefined);

  useEffect(() => {
    document.title = "NimbusIQ — Evolution Timeline";
  }, []);

  const load = useCallback(() => {
    const correlationId = crypto.randomUUID();
    // Set loading state before async operation (intentional sync setState in data fetch pattern)

    setLoading(true);

    setError(undefined);

    controlPlaneApi
      .getTimeline("default", 30, accessToken, correlationId)
      .then((data) => {
        setHistoricalEvents(data.historicalEvents || []);
        // ProjectedEvents API type differs from local interface - needs mapping
        setProjectedEvents(
          (data.projectedEvents || []) as unknown as ProjectedEvent[],
        );
      })
      .catch((err: unknown) => {
        const message = err instanceof Error ? err.message : String(err);
        log.error("Failed to load timeline:", { error: err, correlationId });
        setError(message);
        notify({
          title: "Failed to load timeline",
          body: message,
          intent: "error",
        });
      })
      .finally(() => setLoading(false));
  }, [accessToken, notify]);

  useEffect(() => {
    // Call load function which handles state updates (suppression: load() contains necessary setState calls)
    // eslint-disable-next-line react-hooks/set-state-in-effect
    load();
  }, [load]);

  if (loading) {
    return (
      <div
        style={{ display: "flex", justifyContent: "center", padding: "50px" }}
      >
        <Spinner label="Loading timeline..." />
      </div>
    );
  }

  if (error) {
    return (
      <div className={styles.container}>
        <Text size={900} weight="bold">
          Architecture Evolution Timeline
        </Text>
        <Card className={styles.card}>
          <div
            style={{
              display: "flex",
              flexDirection: "column",
              gap: tokens.spacingVerticalM,
              alignItems: "flex-start",
            }}
          >
            <div
              style={{
                display: "flex",
                alignItems: "center",
                gap: tokens.spacingHorizontalS,
              }}
            >
              <ErrorCircle20Regular
                style={{ color: tokens.colorPaletteRedForeground1 }}
              />
              <Text weight="semibold">Failed to load timeline</Text>
            </div>
            <Text size={300} style={{ color: tokens.colorNeutralForeground3 }}>
              {error}
            </Text>
            <Button
              appearance="secondary"
              icon={<ArrowSyncCircle20Regular />}
              onClick={load}
            >
              Retry
            </Button>
          </div>
        </Card>
      </div>
    );
  }

  if (
    !loading &&
    historicalEvents.length === 0 &&
    projectedEvents.length === 0
  ) {
    return (
      <div className={styles.container}>
        <Text size={900} weight="bold">
          Architecture Evolution Timeline
        </Text>
        <Card className={styles.card}>
          <div
            style={{
              display: "flex",
              flexDirection: "column",
              gap: tokens.spacingVerticalM,
              alignItems: "flex-start",
            }}
          >
            <Text weight="semibold">No timeline data available</Text>
            <Text size={300} style={{ color: tokens.colorNeutralForeground3 }}>
              Run an analysis on a service group to populate the evolution
              timeline.
            </Text>
            <Button
              appearance="secondary"
              icon={<ArrowSyncCircle20Regular />}
              onClick={load}
            >
              Refresh
            </Button>
          </div>
        </Card>
      </div>
    );
  }

  return (
    <div className={styles.container}>
      <Text size={900} weight="bold">
        Architecture Evolution Timeline
      </Text>

      <Card className={styles.card}>
        <div className={styles.section}>
          <Text size={600} weight="semibold">
            Historical Events
          </Text>
        </div>

        <div className={styles.timeline}>
          {historicalEvents.map((event) => {
            const eventKey =
              event.eventType ?? event.eventCategory ?? "analysis";
            const icon =
              categoryIconMap[eventKey] ??
              categoryIconMap[event.eventCategory ?? "analysis"] ??
              "📌";
            const badgeColor =
              categoryColorMap[eventKey] ??
              categoryColorMap[event.eventCategory ?? "analysis"] ??
              "informative";
            return (
              <div key={event.id} className={styles.timelineItem}>
                <div className={styles.timelineDot}>{icon}</div>
                <div className={styles.timelineContent}>
                  <div
                    style={{
                      display: "flex",
                      alignItems: "center",
                      gap: tokens.spacingHorizontalS,
                      marginBottom: tokens.spacingVerticalXS,
                    }}
                  >
                    <Badge appearance="tint" color={badgeColor}>
                      {event.eventType ?? event.eventCategory ?? "analysis"}
                    </Badge>
                    <Text weight="semibold">{event.description}</Text>
                  </div>
                  <Text
                    size={200}
                    style={{ color: tokens.colorNeutralForeground3 }}
                  >
                    {new Date(event.timestamp).toLocaleDateString(undefined, {
                      month: "short",
                      day: "numeric",
                      year: "numeric",
                      hour: "2-digit",
                      minute: "2-digit",
                    })}
                  </Text>
                  <div
                    style={{
                      display: "flex",
                      gap: tokens.spacingHorizontalS,
                      alignItems: "center",
                      marginTop: tokens.spacingVerticalXS,
                    }}
                  >
                    {event.impact && (
                      <Badge
                        appearance="filled"
                        color={
                          event.impact === "high"
                            ? "danger"
                            : event.impact === "medium"
                              ? "warning"
                              : "success"
                        }
                      >
                        {event.impact} impact
                      </Badge>
                    )}
                    {event.scoreImpact != null && (
                      <Tooltip
                        content={`Score: ${event.scoreImpact.toFixed(1)}`}
                        relationship="description"
                      >
                        <Badge appearance="outline" color="informative">
                          {event.scoreImpact > 0 ? "+" : ""}
                          {event.scoreImpact.toFixed(1)}
                        </Badge>
                      </Tooltip>
                    )}
                  </div>
                  {event.deltaSummary && (
                    <Text
                      size={200}
                      style={{
                        color: tokens.colorNeutralForeground3,
                        marginTop: tokens.spacingVerticalXS,
                      }}
                    >
                      {event.deltaSummary}
                    </Text>
                  )}
                </div>
              </div>
            );
          })}

          {/* Now Marker */}
          <div className={styles.nowMarker}>
            <Badge appearance="filled" color="brand">
              NOW
            </Badge>
            <Text weight="semibold" size={300}>
              Current State
            </Text>
          </div>

          {/* Projected future events */}
          {projectedEvents.map((event, index) => (
            <div key={`proj-${index}`} className={styles.timelineItem}>
              <div
                className={styles.timelineDot}
                style={{ borderStyle: "dashed", opacity: 0.7 }}
              >
                🔮
              </div>
              <div className={styles.futureContent}>
                <Text weight="semibold">{event.description}</Text>
                <Text
                  size={200}
                  style={{
                    color: tokens.colorNeutralForeground3,
                    display: "block",
                  }}
                >
                  Projected:{" "}
                  {new Date(event.projectedDate).toLocaleDateString(undefined, {
                    month: "short",
                    day: "numeric",
                    year: "numeric",
                  })}
                </Text>
                <div
                  style={{
                    display: "flex",
                    gap: tokens.spacingHorizontalS,
                    marginTop: tokens.spacingVerticalXS,
                  }}
                >
                  <Badge
                    appearance="tint"
                    color={
                      event.confidence > 0.8
                        ? "success"
                        : event.confidence > 0.6
                          ? "warning"
                          : "danger"
                    }
                  >
                    {(event.confidence * 100).toFixed(0)}% confidence
                  </Badge>
                  <Badge appearance="outline" color="informative">
                    {event.impact} impact
                  </Badge>
                </div>
                {event.rationale && (
                  <Text
                    size={200}
                    style={{
                      color: tokens.colorNeutralForeground3,
                      marginTop: tokens.spacingVerticalXS,
                    }}
                  >
                    {event.rationale}
                  </Text>
                )}
              </div>
            </div>
          ))}
        </div>
      </Card>
    </div>
  );
}
