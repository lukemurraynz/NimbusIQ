import { lazy, Suspense, useEffect, useMemo, useState } from "react";
import { Navigate, Route, Routes } from "react-router-dom";
import {
  Button,
  Badge,
  Spinner,
  Text,
  makeStyles,
  tokens,
} from "@fluentui/react-components";
import { useMsal } from "@azure/msal-react";
import { AppShell } from "./components/AppShell";
import { BladeProvider } from "./components/BladeShell";
import { ConsentBanner } from "./components/ConsentBanner";
import { initializeOpenTelemetry } from "./telemetry/openTelemetry";
import { getConsentStatus, type ConsentStatus } from "./utils/consentUtils";

const DashboardPage = lazy(() =>
  import("./pages/DashboardPage").then((m) => ({ default: m.DashboardPage })),
);
const ServiceGroupAnalysisPage = lazy(() =>
  import("./pages/ServiceGroupAnalysisPage").then((m) => ({
    default: m.ServiceGroupAnalysisPage,
  })),
);
const ServiceGroupHealthPage = lazy(() =>
  import("./pages/ServiceGroupHealthPage").then((m) => ({
    default: m.ServiceGroupHealthPage,
  })),
);
const RecommendationsPage = lazy(() =>
  import("./pages/RecommendationsPage").then((m) => ({
    default: m.RecommendationsPage,
  })),
);
const RecommendationDetailsPage = lazy(() =>
  import("./pages/RecommendationDetailsPage").then((m) => ({
    default: m.RecommendationDetailsPage,
  })),
);
const ValueTrackingPage = lazy(() =>
  import("./pages/ValueTrackingPage").then((m) => ({
    default: m.ValueTrackingPage,
  })),
);
const EvolutionTimelinePage = lazy(() =>
  import("./pages/EvolutionTimelinePage").then((m) => ({
    default: m.EvolutionTimelinePage,
  })),
);
const DriftTimelinePage = lazy(() =>
  import("./pages/DriftTimelinePage").then((m) => ({
    default: m.DriftTimelinePage,
  })),
);
const AgentChatPage = lazy(() =>
  import("./pages/AgentChatPage").then((m) => ({ default: m.AgentChatPage })),
);
const AgentActivityPage = lazy(() =>
  import("./pages/AgentActivityPage").then((m) => ({
    default: m.AgentActivityPage,
  })),
);
const GovernanceNegotiationPage = lazy(() =>
  import("./pages/GovernanceNegotiationPage").then((m) => ({
    default: m.GovernanceNegotiationPage,
  })),
);
const GovernanceConflictsPage = lazy(() =>
  import("./pages/GovernanceConflictsPage").then((m) => ({
    default: m.GovernanceConflictsPage,
  })),
);
const InsightsPage = lazy(() =>
  import("./pages/InsightsPage").then((m) => ({
    default: m.InsightsPage,
  })),
);
import { msalEnabled, loginRequest } from "./auth/msal";
import { useAccessToken } from "./auth/useAccessToken";
import { controlPlaneApi } from "./services/controlPlaneApi";
import { resolvePersonasFromRoles } from "./personas";

/**
 * Masks PII (Personally Identifiable Information) in user display names for privacy.
 * Examples:
 *   "John Doe" → "John D."
 *   "Jane" → "Ja..."
 *   undefined → "User"
 * @param name The full name to mask
 * @returns Masked display name safe for UI rendering
 */
function maskPiiName(name: string | undefined): string {
  if (!name || name.trim().length === 0) {
    return "User";
  }

  const parts = name.trim().split(/\s+/);
  if (parts.length > 1) {
    // "John Doe" → "John D."
    const firstName = parts[0];
    const lastInitial = parts[parts.length - 1][0]?.toUpperCase() ?? "";
    return `${firstName} ${lastInitial}.`;
  }

  // Single name, very short: "John" → "Jo..."
  // This prevents exposing the full name even for single-word names
  return name.slice(0, 2).toLowerCase() + "...";
}

const useStyles = makeStyles({
  headerRight: {
    display: "flex",
    alignItems: "center",
    gap: tokens.spacingHorizontalM,
    flexWrap: "wrap",
    justifyContent: "flex-end",
    textAlign: "right",
  },
  userBlock: {
    display: "flex",
    flexDirection: "column",
    gap: tokens.spacingVerticalXS,
    alignItems: "flex-end",
  },
  subtle: {
    color: tokens.colorNeutralForeground3,
  },
});

function UnauthenticatedApp() {
  return (
    <BladeProvider>
      <AppShell>
        <Suspense fallback={<Spinner label="Loading…" size="large" />}>
          <Routes>
            <Route path="/" element={<DashboardPage />} />
            <Route path="/dashboard" element={<DashboardPage />} />
            <Route
              path="/service-groups"
              element={<ServiceGroupAnalysisPage />}
            />
            <Route
              path="/service-groups/:id/health"
              element={<ServiceGroupHealthPage />}
            />
            <Route path="/recommendations" element={<RecommendationsPage />} />
            <Route
              path="/recommendations/:id"
              element={<RecommendationDetailsPage />}
            />
            <Route path="/value-tracking" element={<ValueTrackingPage />} />
            <Route path="/timeline" element={<EvolutionTimelinePage />} />
            <Route path="/drift" element={<DriftTimelinePage />} />
            <Route path="/chat" element={<AgentChatPage />} />
            <Route path="/agents" element={<AgentActivityPage />} />
            <Route path="/governance" element={<GovernanceNegotiationPage />} />
            <Route
              path="/governance/conflicts"
              element={<GovernanceConflictsPage />}
            />
            <Route path="/insights/:pillar" element={<InsightsPage />} />
            <Route path="*" element={<Navigate to="/" replace />} />
          </Routes>
        </Suspense>
      </AppShell>
    </BladeProvider>
  );
}

function AuthenticatedApp() {
  const styles = useStyles();
  const { instance, accounts } = useMsal();
  const { accessToken, error: tokenError, hasAccount } = useAccessToken();
  const [me, setMe] = useState<{ name?: string; roles: string[] } | undefined>(
    undefined,
  );
  const [meError, setMeError] = useState<string | undefined>(undefined);

  const activeAccount = accounts[0];

  const personas = useMemo(() => {
    if (!me?.roles) return [];
    return resolvePersonasFromRoles(me.roles);
  }, [me]);

  useEffect(() => {
    let cancelled = false;

    async function run() {
      if (!accessToken) return;
      try {
        const result = await controlPlaneApi.getMe(accessToken);
        if (cancelled) return;
        setMe(result);
        setMeError(undefined);
      } catch (e) {
        if (cancelled) return;
        setMe(undefined);
        setMeError(e instanceof Error ? e.message : String(e));
      }
    }

    void run();

    return () => {
      cancelled = true;
    };
  }, [accessToken]);

  async function signIn() {
    await instance.loginPopup(loginRequest);
  }

  async function signOut() {
    if (!activeAccount) return;
    await instance.logoutPopup({ account: activeAccount });
  }

  // Helper to mask email for privacy (COMP-001: PII protection)
  function maskEmail(email: string): string {
    if (!email || !email.includes("@")) return "User";
    const [local] = email.split("@");
    return local.length > 0 ? `${local[0]}***` : "User";
  }

  const headerRight = (
    <div className={styles.headerRight}>
      {tokenError && (
        <Badge appearance="tint" color="danger">
          Token error
        </Badge>
      )}
      {meError && (
        <Badge appearance="tint" color="danger">
          API error
        </Badge>
      )}
      {hasAccount ? (
        <>
          <div className={styles.userBlock}>
            <Text size={300} className={styles.subtle}>
              Signed in as{" "}
              {activeAccount?.name ? maskEmail(activeAccount.name) : "User"}
            </Text>
            <Text size={300}>
              {maskPiiName(me?.name ?? activeAccount?.username)}
            </Text>
            {personas.length > 0 && (
              <Text size={200} className={styles.subtle}>
                Persona: {personas.map((p) => p.label).join(", ")}
              </Text>
            )}
          </div>
          <Button
            appearance="outline"
            style={{
              color: tokens.colorNeutralForegroundInverted,
              borderColor: tokens.colorNeutralForegroundInverted,
            }}
            onClick={signOut}
          >
            Sign out
          </Button>
        </>
      ) : (
        <Button
          appearance="outline"
          style={{
            color: tokens.colorNeutralForegroundInverted,
            borderColor: tokens.colorNeutralForegroundInverted,
          }}
          onClick={signIn}
        >
          Sign in
        </Button>
      )}
    </div>
  );

  return (
    <BladeProvider>
      <AppShell headerRight={headerRight}>
        <Suspense fallback={<Spinner label="Loading…" size="large" />}>
          <Routes>
            <Route path="/" element={<DashboardPage />} />
            <Route path="/dashboard" element={<DashboardPage />} />
            <Route
              path="/service-groups"
              element={<ServiceGroupAnalysisPage />}
            />
            <Route
              path="/service-groups/:id/health"
              element={<ServiceGroupHealthPage />}
            />
            <Route path="/recommendations" element={<RecommendationsPage />} />
            <Route
              path="/recommendations/:id"
              element={<RecommendationDetailsPage />}
            />
            <Route path="/value-tracking" element={<ValueTrackingPage />} />
            <Route path="/timeline" element={<EvolutionTimelinePage />} />
            <Route path="/drift" element={<DriftTimelinePage />} />
            <Route path="/chat" element={<AgentChatPage />} />
            <Route path="/agents" element={<AgentActivityPage />} />
            <Route path="/governance" element={<GovernanceNegotiationPage />} />
            <Route
              path="/governance/conflicts"
              element={<GovernanceConflictsPage />}
            />
            <Route path="/insights/:pillar" element={<InsightsPage />} />
            <Route path="*" element={<Navigate to="/" replace />} />
          </Routes>
        </Suspense>
      </AppShell>
    </BladeProvider>
  );
}

export default function App() {
  const [consentStatus, setConsentStatus] = useState<ConsentStatus>(() =>
    getConsentStatus(),
  );

  useEffect(() => {
    if (consentStatus === "accepted") {
      initializeOpenTelemetry(import.meta.env.VITE_OTLP_ENDPOINT);
    }
  }, [consentStatus]);

  return (
    <>
      {msalEnabled ? <AuthenticatedApp /> : <UnauthenticatedApp />}
      <ConsentBanner onConsentChange={setConsentStatus} />
    </>
  );
}
