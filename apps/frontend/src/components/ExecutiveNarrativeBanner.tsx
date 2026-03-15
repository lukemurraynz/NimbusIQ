import { useEffect, useState } from "react";
import {
  Card,
  Text,
  Badge,
  makeStyles,
  tokens,
  Spinner,
  Tooltip,
} from "@fluentui/react-components";
import { Lightbulb24Regular } from "@fluentui/react-icons";
import DOMPurify from "isomorphic-dompurify";
import {
  controlPlaneApi,
  type ExecutiveNarrative,
} from "../services/controlPlaneApi";

const useStyles = makeStyles({
  banner: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalS,
    padding: tokens.spacingHorizontalL,
    backgroundColor: tokens.colorNeutralBackground1,
    borderLeft: `4px solid ${tokens.colorBrandBackground}`,
  },
  header: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalS,
  },
  highlights: {
    display: "flex",
    flexWrap: "wrap",
    gap: tokens.spacingHorizontalS,
    marginTop: tokens.spacingVerticalXS,
  },
});

const severityColors: Record<
  string,
  "success" | "warning" | "danger" | "informative"
> = {
  success: "success",
  warning: "warning",
  danger: "danger",
  info: "informative",
};

function getNarrativeBadge(source?: string): {
  label: string;
  color: "brand" | "warning";
  tooltip: string;
} | null {
  if (!source) return null;

  if (source === "ai_foundry") {
    return {
      label: "✨ AI-Enhanced",
      color: "brand",
      tooltip:
        "GPT-4 cross-pillar correlation, root-cause analysis, and prioritised actions",
    };
  }

  if (source === "ai_foundry_fallback" || source === "ai_foundry_error") {
    return {
      label: "⚠ AI Fallback",
      color: "warning",
      tooltip:
        "Azure AI Foundry is configured but this run fell back to deterministic analysis.",
    };
  }

  return {
    label: "⚠ Basic Analysis",
    color: "warning",
    tooltip:
      "Basic score summary only — enable Azure AI Foundry for deeper insights",
  };
}

interface ExecutiveNarrativeBannerProps {
  serviceGroupId: string | undefined;
  accessToken?: string;
}

export function ExecutiveNarrativeBanner({
  serviceGroupId,
  accessToken,
}: ExecutiveNarrativeBannerProps) {
  const styles = useStyles();
  const [narrative, setNarrative] = useState<ExecutiveNarrative | null>(null);
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    if (!serviceGroupId) return;
    let cancelled = false;

    async function fetchNarrative() {
      setLoading(true);
      try {
        const res = await controlPlaneApi.getExecutiveNarrative(
          serviceGroupId!,
          accessToken,
        );
        if (!cancelled) setNarrative(res);
      } catch {
        // Graceful degradation — banner just won't show
      } finally {
        if (!cancelled) setLoading(false);
      }
    }

    void fetchNarrative();
    return () => {
      cancelled = true;
    };
  }, [serviceGroupId, accessToken]);

  if (!serviceGroupId || loading) {
    return loading ? <Spinner size="tiny" label="Loading insights..." /> : null;
  }

  if (!narrative) return null;

  const narrativeBadge = getNarrativeBadge(narrative.confidenceSource);

  // Sanitize AI-generated content to prevent XSS attacks
  // Allow only safe text content, no HTML tags or scripts
  const sanitizedSummary = DOMPurify.sanitize(narrative.summary, {
    ALLOWED_TAGS: [], // Strip all HTML tags
    ALLOWED_ATTR: [], // Strip all attributes
    KEEP_CONTENT: true, // Keep text content
  });

  return (
    <Card className={styles.banner}>
      <div className={styles.header}>
        <Lightbulb24Regular />
        <Text weight="semibold" size={400}>
          Executive Summary
        </Text>
        {narrativeBadge && (
          <Tooltip content={narrativeBadge.tooltip} relationship="description">
            <Badge appearance="tint" color={narrativeBadge.color}>
              {narrativeBadge.label}
            </Badge>
          </Tooltip>
        )}
      </div>
      <Text size={300}>{sanitizedSummary}</Text>
      {narrative.highlights.length > 0 && (
        <div className={styles.highlights}>
          {narrative.highlights.map((h, i) => {
            // Sanitize each highlight message to prevent XSS
            const sanitizedMessage = DOMPurify.sanitize(h.message, {
              ALLOWED_TAGS: [],
              ALLOWED_ATTR: [],
              KEEP_CONTENT: true,
            });

            return (
              <Badge
                key={i}
                appearance="outline"
                color={severityColors[h.severity] ?? "informative"}
              >
                {sanitizedMessage}
              </Badge>
            );
          })}
        </div>
      )}
    </Card>
  );
}
