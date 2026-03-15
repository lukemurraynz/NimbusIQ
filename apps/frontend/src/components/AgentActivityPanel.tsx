import {
  Badge,
  Card,
  makeStyles,
  mergeClasses,
  ProgressBar,
  Spinner,
  Text,
  tokens,
} from '@fluentui/react-components';
import {
  BotRegular,
  CheckmarkCircle16Filled,
  DismissCircle16Filled,
  Alert16Regular,
} from '@fluentui/react-icons';
import type { AgentActivity, AgentFinding } from '../hooks/useAgentStream';

interface AgentActivityPanelProps {
  agents: AgentActivity[];
  findings: AgentFinding[];
  runCompleted: boolean;
  overallScore?: number;
  className?: string;
}

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  header: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
    marginBottom: tokens.spacingVerticalXS,
  },
  agentRow: {
    display: 'flex',
    flexDirection: 'column',
    gap: '2px',
    padding: `${tokens.spacingVerticalXS} ${tokens.spacingHorizontalS}`,
    borderRadius: tokens.borderRadiusMedium,
    backgroundColor: tokens.colorNeutralBackground2,
  },
  agentRowRunning: {
    backgroundColor: tokens.colorBrandBackground2,
  },
  agentRowError: {
    backgroundColor: tokens.colorStatusDangerBackground1,
  },
  agentTop: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
    gap: tokens.spacingHorizontalS,
  },
  agentName: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    flexShrink: 0,
  },
  agentMeta: {
    color: tokens.colorNeutralForeground3,
    whiteSpace: 'nowrap',
  },
  findingRow: {
    display: 'flex',
    alignItems: 'flex-start',
    gap: tokens.spacingHorizontalS,
    padding: `2px ${tokens.spacingHorizontalXS}`,
  },
  findingMsg: {
    flex: 1,
    wordBreak: 'break-word',
  },
  divider: {
    borderTop: `1px solid ${tokens.colorNeutralStroke2}`,
    margin: `${tokens.spacingVerticalXS} 0`,
  },
});

const SEVERITY_INTENT: Record<string, 'success' | 'warning' | 'danger' | 'informative'> = {
  critical: 'danger',
  high: 'danger',
  warning: 'warning',
  medium: 'warning',
  low: 'informative',
  info: 'informative',
};

function AgentRow({ agent }: { agent: AgentActivity }) {
  const styles = useStyles();
  return (
    <div
      className={mergeClasses(
        styles.agentRow,
        agent.status === 'running' ? styles.agentRowRunning : undefined,
        agent.status === 'error' ? styles.agentRowError : undefined
      )}
    >
      <div className={styles.agentTop}>
        <div className={styles.agentName}>
          {agent.status === 'running' ? (
            <Spinner size="extra-tiny" />
          ) : agent.status === 'completed' ? (
            <CheckmarkCircle16Filled color={tokens.colorStatusSuccessForeground1} />
          ) : agent.status === 'error' ? (
            <DismissCircle16Filled color={tokens.colorStatusDangerForeground1} />
          ) : (
            <BotRegular fontSize={14} />
          )}
          <Text size={200} weight={agent.status === 'running' ? 'semibold' : 'regular'}>
            {agent.agentName}
          </Text>
        </div>
        <div style={{ display: 'flex', alignItems: 'center', gap: tokens.spacingHorizontalXS }}>
          {agent.scoreValue !== undefined && (
            <Badge appearance="tint" color="informative" size="small">
              {(agent.scoreValue * 100).toFixed(0)}%
            </Badge>
          )}
          {agent.itemsProcessed !== undefined && (
            <Text size={100} className={styles.agentMeta}>
              {agent.itemsProcessed} item{agent.itemsProcessed !== 1 ? 's' : ''}
            </Text>
          )}
          {agent.elapsedMs !== undefined && (
            <Text size={100} className={styles.agentMeta}>
              {agent.elapsedMs < 1000 ? `${agent.elapsedMs}ms` : `${(agent.elapsedMs / 1000).toFixed(1)}s`}
            </Text>
          )}
        </div>
      </div>
      {agent.status === 'running' && (
        <ProgressBar shape="rounded" thickness="medium" />
      )}
      {agent.summary && (
        <Text size={100} style={{ color: tokens.colorNeutralForeground3 }}>
          {agent.summary}
        </Text>
      )}
    </div>
  );
}

/**
 * Real-time agent activity panel.
 * Renders a live feed of agent progress events streamed via SignalR.
 * Mount inside ServiceGroupAnalysisPage once an analysis run has started.
 */
export function AgentActivityPanel({
  agents,
  findings,
  runCompleted,
  overallScore,
  className,
}: AgentActivityPanelProps) {
  const styles = useStyles();

  if (agents.length === 0 && findings.length === 0) return null;

  return (
    <Card className={className} role="region" aria-label="Agent activity" aria-live="polite">
      <div className={styles.root}>
        <div className={styles.header}>
          <BotRegular fontSize={20} />
          <Text size={300} weight="semibold">
            Live Agent Activity
          </Text>
          {!runCompleted && <Spinner size="extra-tiny" />}
          {runCompleted && overallScore !== undefined && (
            <Badge appearance="filled" color="success" size="medium" style={{ marginLeft: 'auto' }}>
              Score: {(overallScore * 100).toFixed(0)}%
            </Badge>
          )}
        </div>

        {agents.map((a) => (
          <AgentRow key={a.agentName} agent={a} />
        ))}

        {findings.length > 0 && (
          <>
            <div className={styles.divider} />
            <Text size={200} weight="semibold" style={{ marginBottom: 2 }}>
              Key Findings
            </Text>
            {findings.map((f, i) => (
              <div key={i} className={styles.findingRow}>
                <Alert16Regular
                  color={
                    f.severity === 'critical' || f.severity === 'high'
                      ? tokens.colorStatusDangerForeground1
                      : f.severity === 'warning' || f.severity === 'medium'
                      ? tokens.colorStatusWarningForeground1
                      : tokens.colorNeutralForeground3
                  }
                  style={{ flexShrink: 0, marginTop: 2 }}
                />
                <div className={styles.findingMsg}>
                  <Badge
                    appearance="tint"
                    color={SEVERITY_INTENT[f.severity] ?? 'informative'}
                    size="extra-small"
                  >
                    {f.severity}
                  </Badge>{' '}
                  <Text size={200}>{f.message}</Text>
                </div>
              </div>
            ))}
          </>
        )}
      </div>
    </Card>
  );
}
