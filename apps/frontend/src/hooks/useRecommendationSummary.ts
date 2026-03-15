import { useCallback, useRef, useState } from 'react';

export interface RecommendationSummaryState {
  text: string;
  streaming: boolean;
  error: string | null;
}

const DEFAULT_BASE_URL = import.meta.env['VITE_CONTROL_PLANE_API_URL'] as string | undefined ?? '';

export function useRecommendationSummary() {
  const abortRef = useRef<AbortController | null>(null);

  const [state, setState] = useState<RecommendationSummaryState>({
    text: '',
    streaming: false,
    error: null,
  });

  const generate = useCallback(async (opts: { baseUrl?: string; accessToken?: string } = {}) => {
    abortRef.current?.abort();
    abortRef.current = new AbortController();
    const signal = abortRef.current.signal;

    setState({ text: '', streaming: true, error: null });

    const baseUrl = opts.baseUrl ?? DEFAULT_BASE_URL;
    const url = `${baseUrl}/api/v1/recommendations/summary`;

    try {
      const response = await fetch(url, {
        method: 'POST',
        headers: {
          'Content-Type': 'application/json',
          ...(opts.accessToken ? { Authorization: `Bearer ${opts.accessToken}` } : {}),
        },
        signal,
      });

      if (!response.ok) {
        const errText = await response.text();
        throw new Error(`HTTP ${response.status}: ${errText}`);
      }

      if (!response.body) throw new Error('No response body');

      const reader = response.body.getReader();
      const decoder = new TextDecoder();
      let buffer = '';

      while (true) {
        const { done, value } = await reader.read();
        if (done) break;

        buffer += decoder.decode(value, { stream: true });
        const lines = buffer.split('\n\n');
        buffer = lines.pop() ?? '';

        for (const block of lines) {
          const dataLine = block.split('\n').find((l) => l.startsWith('data:'));
          if (!dataLine) continue;

          const json = dataLine.slice(5).trim();
          if (!json) continue;

          let event: { type: string; delta?: string; message?: string };
          try {
            event = JSON.parse(json);
          } catch {
            continue;
          }

          if (event.type === 'TEXT_MESSAGE_CONTENT' && event.delta) {
            setState((prev) => ({ ...prev, text: prev.text + event.delta }));
          } else if (event.type === 'RUN_FINISHED') {
            setState((prev) => ({ ...prev, streaming: false }));
          } else if (event.type === 'RUN_ERROR') {
            setState((prev) => ({ ...prev, streaming: false, error: event.message ?? 'Unknown error' }));
          }
        }
      }

      setState((prev) => ({ ...prev, streaming: false }));
    } catch (err) {
      if (err instanceof DOMException && err.name === 'AbortError') return;
      const msg = err instanceof Error ? err.message : String(err);
      setState((prev) => ({ ...prev, streaming: false, error: msg }));
    }
  }, []);

  return { ...state, generate };
}
