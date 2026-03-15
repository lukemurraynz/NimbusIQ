/**
 * GDPR-compliant consent banner for analytics and tracking
 * Addresses COMP-001: Tracking/analytics without consent check
 */

import { useState } from "react";
import {
  MessageBar,
  MessageBarBody,
  MessageBarActions,
  Button,
  Link,
  makeStyles,
  tokens,
} from "@fluentui/react-components";
import {
  type ConsentStatus,
  getConsentStatus,
  setConsentStatus,
} from "../utils/consentUtils";

const useStyles = makeStyles({
  banner: {
    position: "fixed",
    bottom: "0",
    left: "0",
    right: "0",
    zIndex: 1000,
    borderRadius: "0",
    boxShadow: tokens.shadow16,
  },
});

export interface ConsentBannerProps {
  onConsentChange?: (status: ConsentStatus) => void;
}

/**
 * GDPR consent banner component
 * Shows a banner at the bottom of the page until user accepts or declines.
 * State is initialised lazily from localStorage so no effect is needed.
 */
export function ConsentBanner({ onConsentChange }: ConsentBannerProps) {
  const styles = useStyles();

  // Initialise from storage so the banner hides immediately on re-mount
  // when the user has already given/declined consent.
  const [consentStatus, setConsentStatusState] = useState<ConsentStatus>(
    () => getConsentStatus(),
  );

  const handleAccept = () => {
    setConsentStatus("accepted");
    setConsentStatusState("accepted");
    onConsentChange?.("accepted");
  };

  const handleDecline = () => {
    setConsentStatus("declined");
    setConsentStatusState("declined");
    onConsentChange?.("declined");
  };

  // Don't show if user has already made a choice
  if (consentStatus !== "pending") {
    return null;
  }

  return (
    <MessageBar
      intent="info"
      className={styles.banner}
      data-testid="consent-banner"
    >
      <MessageBarBody>
        We use analytics to improve your experience. By clicking "Accept", you
        consent to the use of analytics cookies. You can change your preferences
        at any time. See our{" "}
        <Link href="/privacy" target="_blank">
          Privacy Policy
        </Link>{" "}
        for more details.
      </MessageBarBody>
      <MessageBarActions
        containerAction={
          <>
            <Button
              appearance="primary"
              onClick={handleAccept}
              data-testid="consent-accept"
            >
              Accept
            </Button>
            <Button onClick={handleDecline} data-testid="consent-decline">
              Decline
            </Button>
          </>
        }
      />
    </MessageBar>
  );
}
