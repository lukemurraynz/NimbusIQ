import type { ReactNode } from 'react';
import { makeStyles, tokens } from '@fluentui/react-components';

const useStyles = makeStyles({
  listContainer: {
    backgroundColor: tokens.colorNeutralBackground2,
    flex: 1,
    overflow: 'auto',
  },
  table: {
    width: '100%',
    borderCollapse: 'collapse',
    backgroundColor: tokens.colorNeutralBackground1,
  },
  thead: {
    backgroundColor: tokens.colorNeutralBackground3,
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    position: 'sticky',
    top: 0,
    zIndex: 1,
  },
  th: {
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalL}`,
    textAlign: 'left',
    fontSize: tokens.fontSizeBase200,
    fontWeight: tokens.fontWeightSemibold,
    color: tokens.colorNeutralForeground2,
    textTransform: 'uppercase',
    letterSpacing: '0.02em',
  },
  tbody: {},
  tr: {
    borderBottom: `1px solid ${tokens.colorNeutralStroke2}`,
    cursor: 'pointer',
    transition: 'background-color 0.1s ease',
    '&:hover': {
      backgroundColor: tokens.colorNeutralBackground1Hover,
    },
    '&:active': {
      backgroundColor: tokens.colorNeutralBackground1Pressed,
    },
  },
  td: {
    padding: `${tokens.spacingVerticalM} ${tokens.spacingHorizontalL}`,
    fontSize: tokens.fontSizeBase300,
    color: tokens.colorNeutralForeground1,
  },
  emptyState: {
    padding: tokens.spacingVerticalXXXL,
    textAlign: 'center',
    color: tokens.colorNeutralForeground3,
  },
});

interface Column<T> {
  key: string;
  header: string;
  render: (item: T) => ReactNode;
  width?: string;
}

interface AzureListProps<T> {
  columns: Column<T>[];
  items: T[];
  onItemClick?: (item: T) => void;
  emptyMessage?: string;
}

/**
 * Azure Portal-style list/table component with hover states and row selection.
 * Follows Microsoft design guidelines for data grids.
 */
export function AzureList<T>({ columns, items, onItemClick, emptyMessage = 'No items found' }: AzureListProps<T>) {
  const styles = useStyles();

  if (items.length === 0) {
    return <div className={styles.emptyState} role="status" aria-live="polite">{emptyMessage}</div>;
  }

  return (
    <div className={styles.listContainer} role="region" aria-label="Data table">
      <table className={styles.table} role="table" aria-label="List of items">
        <thead className={styles.thead}>
          <tr role="row">
            {columns.map((col) => (
              <th key={col.key} className={styles.th} style={{ width: col.width }} role="columnheader" scope="col">
                {col.header}
              </th>
            ))}
          </tr>
        </thead>
        <tbody className={styles.tbody}>
          {items.map((item, idx) => (
            <tr
              key={idx}
              className={styles.tr}
              onClick={() => onItemClick?.(item)}
              onKeyDown={(e) => {
                if (e.key === 'Enter' || e.key === ' ') {
                  e.preventDefault();
                  onItemClick?.(item);
                }
              }}
              role="row"
              tabIndex={0}
              aria-rowindex={idx + 2}
            >
              {columns.map((col) => (
                <td key={col.key} className={styles.td} role="cell">
                  {col.render(item)}
                </td>
              ))}
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
