import { useEffect, useState } from 'react';
import { InteractionStatus, type SilentRequest } from '@azure/msal-browser';
import { useMsal } from '@azure/msal-react';
import { config } from '../config';

export function useAccessToken() {
  const { instance, accounts, inProgress } = useMsal();
  const [accessToken, setAccessToken] = useState<string | undefined>(undefined);
  const [error, setError] = useState<string | undefined>(undefined);

  const account = accounts[0];

  useEffect(() => {
    let cancelled = false;

    async function run() {
      if (!config.auth.enabled || !config.auth.apiScope) return;
      if (!account) return;
      if (inProgress !== InteractionStatus.None) return;

      const request: SilentRequest = {
        account,
        scopes: [config.auth.apiScope],
      };

      try {
        const result = await instance.acquireTokenSilent(request);
        if (cancelled) return;
        setAccessToken(result.accessToken);
        setError(undefined);
      } catch (e) {
        if (cancelled) return;
        setAccessToken(undefined);
        setError(e instanceof Error ? e.message : String(e));
      }
    }

    void run();

    return () => {
      cancelled = true;
    };
  }, [account, inProgress, instance]);

  return { accessToken, error, hasAccount: Boolean(account) };
}

