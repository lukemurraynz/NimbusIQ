import type { ReactNode } from "react";
import { NavLink } from "react-router-dom";
import { makeStyles, tokens, Text } from "@fluentui/react-components";
import {
  Home24Regular,
  DataUsage24Regular,
  Lightbulb24Regular,
  Timeline24Regular,
  DataTrending24Regular,
  BotSparkle24Regular,
  ChartMultiple24Regular,
  Gavel24Regular,
  MoneyHand24Regular,
  Warning24Regular,
} from "@fluentui/react-icons";
import { SkipToContent } from "./SkipToContent";
import { ConsentBanner } from "./ConsentBanner";

const useStyles = makeStyles({
  root: {
    minHeight: "100vh",
    backgroundColor: tokens.colorNeutralBackground3,
    color: tokens.colorNeutralForeground1,
    display: "flex",
    flexDirection: "column",
  },
  header: {
    height: "50px",
    display: "flex",
    alignItems: "center",
    justifyContent: "space-between",
    padding: `0 ${tokens.spacingHorizontalL}`,
    backgroundColor: tokens.colorBrandBackground,
    color: tokens.colorNeutralForegroundInverted,
    boxShadow: tokens.shadow4,
    position: "sticky",
    top: 0,
    zIndex: 100,
  },
  brand: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalM,
  },
  brandIcon: {
    width: "32px",
    height: "32px",
    backgroundColor: tokens.colorNeutralForegroundInverted,
    borderRadius: tokens.borderRadiusMedium,
    display: "flex",
    alignItems: "center",
    justifyContent: "center",
    fontSize: "18px",
    fontWeight: "bold",
    color: tokens.colorBrandBackground,
  },
  brandTitle: {
    fontSize: tokens.fontSizeBase400,
    fontWeight: tokens.fontWeightSemibold,
    letterSpacing: "-0.01em",
  },
  layout: {
    display: "flex",
    flex: 1,
    overflow: "hidden",
  },
  sidebar: {
    width: "64px",
    backgroundColor: tokens.colorNeutralBackground1,
    borderRight: `1px solid ${tokens.colorNeutralStroke2}`,
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    paddingTop: tokens.spacingVerticalM,
    gap: tokens.spacingVerticalXS,
    flexShrink: 0,
  },
  nav: {
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    gap: tokens.spacingVerticalXS,
    width: "100%",
  },
  navItem: {
    width: "56px",
    minHeight: "52px",
    display: "flex",
    flexDirection: "column",
    alignItems: "center",
    justifyContent: "center",
    gap: "2px",
    borderRadius: tokens.borderRadiusMedium,
    textDecorationLine: "none",
    color: tokens.colorNeutralForeground3,
    transition: "all 0.2s ease",
    position: "relative",
    paddingTop: "6px",
    paddingBottom: "6px",
    "&:hover": {
      backgroundColor: tokens.colorNeutralBackground1Hover,
      color: tokens.colorNeutralForeground1,
    },
  },
  navItemActive: {
    backgroundColor: tokens.colorSubtleBackgroundSelected,
    color: tokens.colorBrandForeground1,
    "&::before": {
      content: '""',
      position: "absolute",
      left: 0,
      top: "8px",
      bottom: "8px",
      width: "3px",
      backgroundColor: tokens.colorBrandForeground1,
      borderRadius: "0 2px 2px 0",
    },
  },
  navLabel: {
    fontSize: "9px",
    lineHeight: "12px",
    textAlign: "center",
    whiteSpace: "nowrap",
    overflow: "hidden",
    maxWidth: "52px",
    textOverflow: "ellipsis",
  },
  content: {
    flex: 1,
    backgroundColor: tokens.colorNeutralBackground2,
    overflow: "hidden",
    position: "relative",
  },
});

type AppShellProps = {
  headerRight?: ReactNode;
  children: ReactNode;
};

/**
 * Azure Portal-style shell with top bar and compact icon navigation.
 * Uses Fluent UI design tokens for consistent Microsoft styling.
 */
export function AppShell(props: AppShellProps) {
  const styles = useStyles();

  return (
    <div className={styles.root}>
      <SkipToContent />
      {/* Azure Portal-style top bar */}
      <header className={styles.header} role="banner">
        <div className={styles.brand}>
          <div className={styles.brandIcon} aria-hidden="true">
            A
          </div>
          <Text className={styles.brandTitle}>NimbusIQ</Text>
        </div>
        <div>{props.headerRight}</div>
      </header>

      {/* Main layout with icon sidebar and content */}
      <div className={styles.layout}>
        {/* Compact icon navigation (Azure Portal style) */}
        <aside
          className={styles.sidebar}
          role="navigation"
          aria-label="Main navigation"
        >
          <nav className={styles.nav}>
            <NavLink
              to="/dashboard"
              className={({ isActive }) =>
                isActive
                  ? `${styles.navItem} ${styles.navItemActive}`
                  : styles.navItem
              }
              title="Dashboard"
              aria-label="Dashboard"
            >
              <Home24Regular aria-hidden="true" />
              <span className={styles.navLabel}>Dashboard</span>
            </NavLink>
            <NavLink
              to="/service-groups"
              className={({ isActive }) =>
                isActive
                  ? `${styles.navItem} ${styles.navItemActive}`
                  : styles.navItem
              }
              title="Service Groups"
              aria-label="Service Groups"
            >
              <DataUsage24Regular aria-hidden="true" />
              <span className={styles.navLabel}>Groups</span>
            </NavLink>
            <NavLink
              to="/recommendations"
              className={({ isActive }) =>
                isActive
                  ? `${styles.navItem} ${styles.navItemActive}`
                  : styles.navItem
              }
              title="Recommendations"
              aria-label="Recommendations"
            >
              <Lightbulb24Regular aria-hidden="true" />
              <span className={styles.navLabel}>Recs</span>
            </NavLink>
            <NavLink
              to="/value-tracking"
              className={({ isActive }) =>
                isActive
                  ? `${styles.navItem} ${styles.navItemActive}`
                  : styles.navItem
              }
              title="Value Realization"
              aria-label="Value Realization"
            >
              <MoneyHand24Regular aria-hidden="true" />
              <span className={styles.navLabel}>Value</span>
            </NavLink>
            <NavLink
              to="/timeline"
              className={({ isActive }) =>
                isActive
                  ? `${styles.navItem} ${styles.navItemActive}`
                  : styles.navItem
              }
              title="Evolution Timeline"
              aria-label="Evolution Timeline"
            >
              <Timeline24Regular aria-hidden="true" />
              <span className={styles.navLabel}>Timeline</span>
            </NavLink>
            <NavLink
              to="/drift"
              className={({ isActive }) =>
                isActive
                  ? `${styles.navItem} ${styles.navItemActive}`
                  : styles.navItem
              }
              title="Drift Timeline"
              aria-label="Drift Timeline"
            >
              <DataTrending24Regular aria-hidden="true" />
              <span className={styles.navLabel}>Drift</span>
            </NavLink>
            <NavLink
              to="/chat"
              className={({ isActive }) =>
                isActive
                  ? `${styles.navItem} ${styles.navItemActive}`
                  : styles.navItem
              }
              title="AI Assistant"
              aria-label="AI Assistant"
            >
              <BotSparkle24Regular aria-hidden="true" />
              <span className={styles.navLabel}>AI Chat</span>
            </NavLink>
            <NavLink
              to="/agents"
              className={({ isActive }) =>
                isActive
                  ? `${styles.navItem} ${styles.navItemActive}`
                  : styles.navItem
              }
              title="Agent Orchestration"
              aria-label="Agent Orchestration"
            >
              <ChartMultiple24Regular aria-hidden="true" />
              <span className={styles.navLabel}>Agents</span>
            </NavLink>
            <NavLink
              to="/governance"
              className={({ isActive }) =>
                isActive
                  ? `${styles.navItem} ${styles.navItemActive}`
                  : styles.navItem
              }
              title="Governance Negotiation"
              aria-label="Governance Negotiation"
            >
              <Gavel24Regular aria-hidden="true" />
              <span className={styles.navLabel}>Governance</span>
            </NavLink>
            <NavLink
              to="/governance/conflicts"
              className={({ isActive }) =>
                isActive
                  ? `${styles.navItem} ${styles.navItemActive}`
                  : styles.navItem
              }
              title="Governance Conflicts"
              aria-label="Governance Conflicts"
            >
              <Warning24Regular aria-hidden="true" />
              <span className={styles.navLabel}>Conflicts</span>
            </NavLink>
          </nav>
        </aside>

        {/* Main content area */}
        <main id="main-content" className={styles.content} role="main">
          {props.children}
        </main>
      </div>
      {/* GDPR consent banner (COMP-001 fix) */}
      <ConsentBanner />
    </div>
  );
}
