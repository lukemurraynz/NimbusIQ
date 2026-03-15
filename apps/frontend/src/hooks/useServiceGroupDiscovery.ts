import { useCallback, useEffect, useState } from "react";
import {
  controlPlaneApi,
  type ServiceGroup,
} from "../services/controlPlaneApi";
import { log } from "../telemetry/logger";
import { config } from "../config";

/**
 * Custom hook to manage service group discovery and listing.
 * Encapsulates service group loading and Azure discovery logic.
 */
export function useServiceGroupDiscovery(accessToken?: string): {
  serviceGroups: ServiceGroup[];
  loading: boolean;
  discovering: boolean;
  discoverResult: {
    discovered: number;
    created: number;
    updated: number;
  } | null;
  error?: string;
  loadServiceGroups: () => void;
  discoverFromAzure: () => Promise<void>;
} {
  const [serviceGroups, setServiceGroups] = useState<ServiceGroup[]>([]);
  const [loading, setLoading] = useState(true);
  const [discovering, setDiscovering] = useState(false);
  const [discoverResult, setDiscoverResult] = useState<{
    discovered: number;
    created: number;
    updated: number;
  } | null>(null);
  const [error, setError] = useState<string | undefined>(undefined);

  const loadServiceGroups = useCallback(() => {
    if (config.auth.enabled && !accessToken) {
      setLoading(false);
      setError("Sign in to load service groups.");
      return;
    }

    const correlationId = crypto.randomUUID();
    setLoading(true);
    setError(undefined);

    controlPlaneApi
      .listServiceGroups(accessToken, correlationId)
      .then((data) => {
        setServiceGroups((data.value || []) as ServiceGroup[]);
      })
      .catch((err: unknown) => {
        log.error("Failed to load service groups:", { error: err, correlationId });
        setError(
          err instanceof Error ? err.message : "Failed to load service groups",
        );
      })
      .finally(() => setLoading(false));
  }, [accessToken]);

  useEffect(() => {
    loadServiceGroups();
  }, [loadServiceGroups]);

  const discoverFromAzure = async () => {
    if (config.auth.enabled && !accessToken) {
      setError("Sign in to discover Azure Service Groups.");
      return;
    }

    setDiscovering(true);
    setDiscoverResult(null);
    setError(undefined);
    const correlationId = crypto.randomUUID();

    try {
      const result = await controlPlaneApi.discoverServiceGroups(
        accessToken,
        correlationId,
      );
      setDiscoverResult({
        discovered: result.discovered,
        created: result.created,
        updated: result.updated,
      });
      // Refresh list after discovery
      loadServiceGroups();
    } catch (err: unknown) {
      log.error("Azure Service Group discovery failed:", { error: err, correlationId });
      setError(
        err instanceof Error
          ? err.message
          : "Discovery failed. Check that the managed identity has Service Group Reader role.",
      );
    } finally {
      setDiscovering(false);
    }
  };

  return {
    serviceGroups,
    loading,
    discovering,
    discoverResult,
    error,
    loadServiceGroups,
    discoverFromAzure,
  };
}
