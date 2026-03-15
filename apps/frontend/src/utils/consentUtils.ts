/**
 * Consent utilities for GDPR-compliant analytics consent management.
 * Extracted to a separate file so ConsentBanner.tsx only exports React components
 * (required for react-refresh fast reload compatibility).
 */

export const CONSENT_STORAGE_KEY = "nimbusiq_analytics_consent";

export type ConsentStatus = "pending" | "accepted" | "declined";

/**
 * Read consent status from localStorage.
 * Returns "pending" if no preference has been stored yet or if
 * localStorage is unavailable (e.g. private browsing mode).
 */
export function getConsentStatus(): ConsentStatus {
  try {
    const stored = localStorage.getItem(CONSENT_STORAGE_KEY);
    if (stored === "accepted" || stored === "declined") {
      return stored;
    }
  } catch {
    // localStorage not available
  }
  return "pending";
}

/**
 * Persist consent status to localStorage.
 */
export function setConsentStatus(status: ConsentStatus): void {
  try {
    localStorage.setItem(CONSENT_STORAGE_KEY, status);
  } catch {
    // localStorage not available
  }
}
