import {
  Badge,
  InfoLabel,
  makeStyles,
  ProgressBar,
  Text,
  tokens,
  Tooltip,
} from '@fluentui/react-components';
import { ShieldCheckmark16Regular, Warning16Regular } from '@fluentui/react-icons';

interface ConfidenceDisclosureProps {
  confidenceScore: number;
  confidenceLevel?: string;
  degradationFactors?: string[];
  /** Show a compact inline version (e.g. in recommendation cards) */
  compact?: boolean;
  /** Additional class name */
  className?: string;
}

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  rootCompact: {
    flexDirection: 'row',
    alignItems: 'center',
    flexWrap: 'wrap',
    gap: tokens.spacingHorizontalXS,
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
  },
  barContainer: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  factorList: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    paddingLeft: tokens.spacingHorizontalS,
  },
  factorItem: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: tokens.spacingHorizontalXS,
    color: tokens.colorStatusWarningForeground1,
  },
});

const LEVEL_COLOR: Record<string, 'success' | 'warning' | 'danger' | 'informative'> = {
  high: 'success',
  medium: 'warning',
  low: 'danger',
};

/**
 * Responsible AI confidence disclosure widget.
 *
 * Renders the AI confidence score visibly on recommendation cards, along with
 * any degradation factors that reduced the score from its maximum.
 * This component directly supports the "Responsible AI" requirement from
 * the hackathon evaluation criteria.
 */
export function ConfidenceDisclosure({
  confidenceScore,
  confidenceLevel,
  degradationFactors = [],
  compact = false,
  className,
}: ConfidenceDisclosureProps) {
  const styles = useStyles();
  const pct = Math.round(confidenceScore * 100);
  const level = confidenceLevel ?? (pct >= 70 ? 'high' : pct >= 40 ? 'medium' : 'low');
  const color = LEVEL_COLOR[level] ?? 'informative';

  if (compact) {
    return (
      <div className={`${styles.rootCompact}${className ? ` ${className}` : ''}`}>
        <ShieldCheckmark16Regular style={{ flexShrink: 0, color: tokens.colorNeutralForeground3 }} />
        <Tooltip
          content={
            degradationFactors.length > 0
              ? `Confidence factors: ${degradationFactors.join('; ')}`
              : `AI confidence: ${pct}%`
          }
          relationship="description"
        >
          <Badge appearance="tint" color={color} size="small">
            {pct}% confidence
          </Badge>
        </Tooltip>
        {degradationFactors.length > 0 && (
          <Tooltip
            content={degradationFactors.join('; ')}
            relationship="description"
          >
            <Warning16Regular
              style={{ color: tokens.colorStatusWarningForeground1, flexShrink: 0 }}
              aria-label={`${degradationFactors.length} degradation factor${degradationFactors.length !== 1 ? 's' : ''}`}
            />
          </Tooltip>
        )}
      </div>
    );
  }

  return (
    <div className={`${styles.root}${className ? ` ${className}` : ''}`}>
      <div className={styles.header}>
        <ShieldCheckmark16Regular style={{ color: tokens.colorNeutralForeground3 }} />
        <InfoLabel
          size="small"
          info={
            'AI confidence reflects the quality and completeness of the underlying Azure resource data. ' +
            'Degradation factors reduce confidence when data is missing or telemetry is unavailable. ' +
            'Always review recommendations in context before applying changes.'
          }
        >
          <Text size={200} weight="semibold">
            AI Confidence
          </Text>
        </InfoLabel>
        <Badge appearance="tint" color={color} size="small" style={{ marginLeft: 'auto' }}>
          {level.toUpperCase()} — {pct}%
        </Badge>
      </div>

      <div className={styles.barContainer}>
        <ProgressBar
          value={confidenceScore}
          color={color === 'success' ? 'success' : color === 'warning' ? 'warning' : 'error'}
          shape="rounded"
          thickness="medium"
          style={{ flex: 1 }}
        />
      </div>

      {degradationFactors.length > 0 && (
        <div className={styles.factorList} role="list" aria-label="Degradation factors">
          {degradationFactors.map((factor, i) => (
            <div key={i} className={styles.factorItem} role="listitem">
              <Warning16Regular style={{ flexShrink: 0, marginTop: 2 }} />
              <Text size={100}>{factor}</Text>
            </div>
          ))}
        </div>
      )}
    </div>
  );
}
