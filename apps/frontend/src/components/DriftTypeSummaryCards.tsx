import { useEffect, useState } from "react";
import {
  Card,
  CardHeader,
  Text,
  Badge,
  makeStyles,
  tokens,
  Spinner,
} from "@fluentui/react-components";
import {
  ShieldError24Regular,
  Money24Regular,
  DocumentCheckmark24Regular,
  TopSpeed24Regular,
  Settings24Regular,
} from "@fluentui/react-icons";
import {
  controlPlaneApi,
  type DriftCategorySummary,
} from "../services/controlPlaneApi";

const useStyles = makeStyles({
  grid: {
    display: "grid",
    gridTemplateColumns: "repeat(auto-fill, minmax(200px, 1fr))",
    gap: tokens.spacingHorizontalM,
  },
  card: {
    padding: tokens.spacingHorizontalM,
    cursor: "pointer",
    ":hover": {
      boxShadow: tokens.shadow4,
    },
  },
  count: {
    fontSize: tokens.fontSizeHero700,
    fontWeight: tokens.fontWeightBold,
  },
  badges: {
    display: "flex",
    gap: tokens.spacingHorizontalXS,
    marginTop: tokens.spacingVerticalXS,
  },
});

const categoryMeta: Record<
  string,
  { label: string; icon: React.ReactNode; color: string }
> = {
  SecurityDrift: {
    label: "Security",
    icon: <ShieldError24Regular />,
    color: "#D13438",
  },
  CostDrift: { label: "Cost", icon: <Money24Regular />, color: "#107C10" },
  ComplianceDrift: {
    label: "Compliance",
    icon: <DocumentCheckmark24Regular />,
    color: "#8764B8",
  },
  PerformanceDrift: {
    label: "Performance",
    icon: <TopSpeed24Regular />,
    color: "#D83B01",
  },
  ConfigurationDrift: {
    label: "Configuration",
    icon: <Settings24Regular />,
    color: "#0078D4",
  },
};

interface DriftTypeSummaryCardsProps {
  serviceGroupId: string | undefined;
  accessToken?: string;
  onCategoryClick?: (category: string) => void;
}

export function DriftTypeSummaryCards({
  serviceGroupId,
  accessToken,
  onCategoryClick,
}: DriftTypeSummaryCardsProps) {
  const styles = useStyles();
  const [categories, setCategories] = useState<DriftCategorySummary[]>([]);
  const [loading, setLoading] = useState(false);

  useEffect(() => {
    if (!serviceGroupId) return;
    let cancelled = false;

    async function fetchCategories() {
      setLoading(true);
      try {
        const res = await controlPlaneApi.getDriftCategories(serviceGroupId!, accessToken);
        if (!cancelled) setCategories(res);
      } catch {
        // silently ignore
      } finally {
        if (!cancelled) setLoading(false);
      }
    }

    void fetchCategories();
    return () => { cancelled = true; };
  }, [serviceGroupId, accessToken]);

  if (loading)
    return <Spinner size="tiny" label="Loading drift categories..." />;
  if (!serviceGroupId || categories.length === 0) return null;

  return (
    <div className={styles.grid}>
      {categories.map((cat) => {
        const meta =
          categoryMeta[cat.category] ?? categoryMeta.ConfigurationDrift;
        return (
          <Card
            key={cat.category}
            className={styles.card}
            onClick={() => onCategoryClick?.(cat.category)}
          >
            <CardHeader
              image={<span>{meta.icon}</span>}
              header={<Text weight="semibold">{meta.label} Drift</Text>}
            />
            <Text
              className={styles.count}
              style={{ color: cat.violationCount > 0 ? meta.color : undefined }}
            >
              {cat.violationCount}
            </Text>
            <Text size={200}>
              violation{cat.violationCount !== 1 ? "s" : ""}
            </Text>
            {(cat.criticalCount > 0 || cat.highCount > 0) && (
              <div className={styles.badges}>
                {cat.criticalCount > 0 && (
                  <Badge appearance="filled" color="danger">
                    {cat.criticalCount} critical
                  </Badge>
                )}
                {cat.highCount > 0 && (
                  <Badge appearance="filled" color="warning">
                    {cat.highCount} high
                  </Badge>
                )}
              </div>
            )}
          </Card>
        );
      })}
    </div>
  );
}
