import { useCallback, useEffect, useState } from 'react';
import { ApiError, api } from '../api/client';
import type { QuoteStatusView } from '../api/types';

export interface AgentQuotesOptions {
  /** How often to re-read the list while any quote is still in underwriting. */
  refreshMs?: number;
  /**
   * Stop refreshing automatically after this many consecutive refreshes with a
   * quote still in flight. Underwriting takes seconds; a quote stuck in Processing
   * (worker down, a dead-lettered message) must not keep this page polling for as
   * long as the tab stays open.
   */
  maxAutoRefreshes?: number;
}

export interface AgentQuotesState {
  /** Newest first. */
  quotes: QuoteStatusView[];
  isLoading: boolean;
  error: ApiError | Error | null;
  /** True while the list is refreshing itself because a quote is in underwriting. */
  isAutoRefreshing: boolean;
  /** True when refreshing stopped at the cap with a quote still in flight. */
  autoRefreshPaused: boolean;
  /** Re-reads now and restarts automatic refreshing. */
  refresh: () => void;
}

/**
 * The signed-in agent's quotes, kept current while underwriting runs.
 *
 * Refreshes only while a quote is non-terminal, driven by the server's own
 * `isTerminal` flag — the same rule as the single-quote status page — and goes
 * quiet once every quote has an outcome.
 */
export function useAgentQuotes(
  agentId: string | null,
  { refreshMs = 3000, maxAutoRefreshes = 40 }: AgentQuotesOptions = {},
): AgentQuotesState {
  const [quotes, setQuotes] = useState<QuoteStatusView[]>([]);
  const [hasLoaded, setHasLoaded] = useState(false);
  const [error, setError] = useState<ApiError | Error | null>(null);
  const [autoRefreshPaused, setAutoRefreshPaused] = useState(false);
  const [refreshToken, setRefreshToken] = useState(0);

  const refresh = useCallback(() => setRefreshToken((token) => token + 1), []);

  useEffect(() => {
    if (!agentId) {
      return;
    }

    // Aborts the in-flight read on teardown, so a response after sign-out or
    // unmount cannot set state.
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    let cancelled = false;
    let autoRefreshes = 0;

    async function load(): Promise<void> {
      try {
        const response = await api.getMyQuotes(controller.signal);
        if (cancelled) {
          return;
        }

        setQuotes(response.items);
        setError(null);
        setHasLoaded(true);

        if (!response.items.some((quote) => !quote.isTerminal)) {
          setAutoRefreshPaused(false);
          return;
        }

        if (autoRefreshes >= maxAutoRefreshes) {
          setAutoRefreshPaused(true);
          return;
        }

        autoRefreshes += 1;
        setAutoRefreshPaused(false);
        timer = setTimeout(load, refreshMs);
      } catch (loadError) {
        if (cancelled || loadError instanceof DOMException) {
          return;
        }

        setHasLoaded(true);
        setError(loadError as Error);
      }
    }

    void load();

    return () => {
      cancelled = true;
      controller.abort();
      if (timer) {
        clearTimeout(timer);
      }
    };
  }, [agentId, refreshToken, refreshMs, maxAutoRefreshes]);

  return {
    quotes,
    isLoading: Boolean(agentId) && !hasLoaded,
    error,
    // Derived, so it cannot disagree with the list it describes.
    isAutoRefreshing: quotes.some((quote) => !quote.isTerminal) && !autoRefreshPaused && !error,
    autoRefreshPaused,
    refresh,
  };
}
