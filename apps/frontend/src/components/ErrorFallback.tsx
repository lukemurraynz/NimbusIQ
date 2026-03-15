import { Button, Text, makeStyles, tokens } from '@fluentui/react-components';
import { ErrorCircle24Regular } from '@fluentui/react-icons';

const useStyles = makeStyles({
  container: {
    display: 'flex',
    flexDirection: 'column',
    alignItems: 'center',
    justifyContent: 'center',
    minHeight: '400px',
    gap: tokens.spacingVerticalL,
    padding: tokens.spacingHorizontalXXL,
    textAlign: 'center',
  },
  icon: {
    fontSize: '48px',
    color: tokens.colorPaletteRedForeground1,
  },
});

export function ErrorFallback({ error, onReset }: { error?: Error; onReset: () => void }) {
  const styles = useStyles();

  return (
    <div className={styles.container} role="alert">
      <ErrorCircle24Regular className={styles.icon} aria-hidden="true" />
      <Text size={500} weight="semibold">
        Something went wrong
      </Text>
      <Text size={300} style={{ color: tokens.colorNeutralForeground3, maxWidth: '480px' }}>
        {error?.message ?? 'An unexpected error occurred. Please try again.'}
      </Text>
      <Button appearance="primary" onClick={onReset}>
        Reload Page
      </Button>
    </div>
  );
}
