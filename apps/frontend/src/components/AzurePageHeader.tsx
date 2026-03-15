import type { ReactNode } from 'react';
import { Text, makeStyles, tokens } from '@fluentui/react-components';

const useStyles = makeStyles({
  header: {
    padding: `${tokens.spacingVerticalXL} ${tokens.spacingHorizontalXXL}`,
    backgroundColor: tokens.colorNeutralBackground1,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
  },
  titleRow: {
    display: 'flex',
    justifyContent: 'space-between',
    alignItems: 'flex-start',
    marginBottom: tokens.spacingVerticalM,
  },
  titleSection: {
    display: 'flex',
    flexDirection: 'column',
    gap: tokens.spacingVerticalXS,
  },
  title: {
    fontSize: tokens.fontSizeBase600,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground1,
  },
  subtitle: {
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground3,
  },
  commandBar: {
    display: 'flex',
    gap: tokens.spacingHorizontalS,
    alignItems: 'center',
  },
  breadcrumb: {
    display: 'flex',
    alignItems: 'center',
    gap: tokens.spacingHorizontalXS,
    fontSize: tokens.fontSizeBase200,
    color: tokens.colorNeutralForeground3,
    marginBottom: tokens.spacingVerticalS,
  },
});

interface AzurePageHeaderProps {
  title: string;
  subtitle?: string;
  breadcrumbs?: ReactNode;
  commands?: ReactNode;
  children?: ReactNode;
}

/**
 * Azure Portal-style page header with breadcrumbs, title, and command bar.
 * Follows Microsoft design guidelines for consistency.
 */
export function AzurePageHeader({ title, subtitle, breadcrumbs, commands, children }: AzurePageHeaderProps) {
  const styles = useStyles();

  return (
    <div className={styles.header} role="region" aria-label="Page header">
      {breadcrumbs && (
        <nav className={styles.breadcrumb} aria-label="Breadcrumb">
          {breadcrumbs}
        </nav>
      )}
      <div className={styles.titleRow}>
        <div className={styles.titleSection}>
          <Text className={styles.title} as="h1">
            {title}
          </Text>
          {subtitle && (
            <Text className={styles.subtitle} as="p">
              {subtitle}
            </Text>
          )}
        </div>
        {commands && (
          <div className={styles.commandBar} role="toolbar" aria-label="Page actions">
            {commands}
          </div>
        )}
      </div>
      {children}
    </div>
  );
}
