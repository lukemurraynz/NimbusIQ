import { useCallback, useEffect, useState } from 'react';
import { Badge, Card, Spinner, Text, makeStyles, tokens } from '@fluentui/react-components';
import { controlPlaneApi } from '../services/controlPlaneApi';

const useStyles = makeStyles({
  root: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalS,
  },
  event: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
    padding: tokens.spacingVerticalS,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    '&:last-child': { borderBottom: 'none' },
  },
  eventHeader: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'center',
  },
  subtle: {
    color: tokens.colorNeutralForeground3,
  },
  empty: {
    padding: tokens.spacingVerticalM,
    textAlign: 'center' as const,
  },
});

interface AuditEvent {
  id: string;
  eventName?: string;
  actorType?: string;
  actorId?: string;
  eventPayload?: string;
  timestamp: string;
}

export function AuditTrail(props: {
  entityType: string;
  entityId: string;
  accessToken?: string;
}) {
  const styles = useStyles();
  const [events, setEvents] = useState<AuditEvent[]>([]);
  const [loading, setLoading] = useState(true);

  const load = useCallback(async () => {
    setLoading(true);
    try {
      const result = await controlPlaneApi.getAuditEvents(
        { entityType: props.entityType, entityId: props.entityId, maxResults: 20 },
        props.accessToken,
      );
      setEvents(result.value ?? []);
    } catch {
      setEvents([]);
    } finally {
      setLoading(false);
    }
  }, [props.entityType, props.entityId, props.accessToken]);

  useEffect(() => {
    void load();
  }, [load]);

  const formatTimestamp = (iso: string) => {
    try {
      return new Date(iso).toLocaleString();
    } catch {
      return iso;
    }
  };

  const eventColor = (name?: string): 'brand' | 'success' | 'danger' | 'informative' => {
    const lower = (name ?? '').toLowerCase();
    if (lower.includes('approve')) return 'success';
    if (lower.includes('reject') || lower.includes('delete')) return 'danger';
    if (lower.includes('create') || lower.includes('update')) return 'brand';
    return 'informative';
  };

  return (
    <Card>
      <Text size={600} weight="semibold">
        Audit Trail
      </Text>

      {loading && <Spinner size="tiny" label="Loading audit events..." />}

      {!loading && events.length === 0 && (
        <div className={styles.empty}>
          <Text className={styles.subtle} size={200}>
            No audit events recorded yet.
          </Text>
        </div>
      )}

      {!loading && events.length > 0 && (
        <div className={styles.root}>
          {events.map((evt) => (
            <div key={evt.id} className={styles.event}>
              <div className={styles.eventHeader}>
                <Badge appearance="outline" color={eventColor(evt.eventName)}>
                  {evt.eventName ?? 'Unknown event'}
                </Badge>
                <Text className={styles.subtle} size={200}>
                  {formatTimestamp(evt.timestamp)}
                </Text>
              </div>
              <Text className={styles.subtle} size={200}>
                {evt.actorType ?? 'system'}: {evt.actorId ?? 'unknown'}
              </Text>
            </div>
          ))}
        </div>
      )}
    </Card>
  );
}
