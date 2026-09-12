import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError, api } from '../api/client';
import type { QuoteComparison, QuoteStatusView } from '../api/types';

/** How often to re-check an in-flight quote. */
const POLL_INTERVAL_MS = 1500;

/**
 * Give up after this many attempts.
 *
 * At 1.5s that is 45 seconds. Underwriting normally finishes in 1-2 seconds, so
 * reaching this means something is genuinely wrong — the worker is down, or the
 * outbox is not draining. Without a cap the page would poll forever, which both
 * hides the failure from the user and keeps hitting a broken backend.
 */
const MAX_POLL_ATTEMPTS = 30;

export interface QuotePollingState {
  quote: QuoteStatusView | null;
  comparison: QuoteComparison | null;
  /** True on the very first load, before any status is known. */
  isLoading: boolean;
  /** True while underwriting is still in flight and polling continues. */
  isPolling: boolean;
  error: ApiError | Error | null;
  /** True when polling stopped without reaching a terminal status. */
  timedOut: boolean;
  /** Number of status reads performed. Shown in the demo. */
  attempts: number;
  /** Re-fetches immediately, resetting the timeout counter. */
  refresh: () => void;
}

/**
 * Polls a quote until underwriting reaches a terminal status, then fetches the
 * carrier comparison once.
 *
 * Polling, not websockets, on purpose. It is the right amount of machinery for a
 * workflow that finishes in about two seconds: no connection to keep alive, no
 * reconnection logic, no sticky-session requirement on a scaled-out API, and the
 * status endpoint is a single indexed primary-key read.
 */
export function useQuotePolling(quoteId: string | undefined): QuotePollingState {
  const [quote, setQuote] = useState<QuoteStatusView | null>(null);
  const [comparison, setComparison] = useState<QuoteComparison | null>(null);
  const [isLoading, setIsLoading] = useState(Boolean(quoteId));
  const [error, setError] = useState<ApiError | Error | null>(null);
  const [timedOut, setTimedOut] = useState(false);
  const [attempts, setAttempts] = useState(0);
  const [refreshToken, setRefreshToken] = useState(0);

  // Held in a ref rather than state: the polling loop reads it on every tick, and
  // putting it in state would restart the effect on each increment.
  const attemptsRef = useRef(0);

  const refresh = useCallback(() => {
    attemptsRef.current = 0;
    setTimedOut(false);
    setError(null);
    setRefreshToken((token) => token + 1);
  }, []);

  useEffect(() => {
    if (!quoteId) {
      // No setState here: isLoading is initialised from Boolean(quoteId) and is
      // derived again on return, so there is nothing to synchronise.
      return;
    }

    // Cancels the in-flight request when the effect is torn down, so a response
    // that lands after unmount cannot set state on a dead component.
    const controller = new AbortController();
    let timer: ReturnType<typeof setTimeout> | undefined;
    let cancelled = false;

    // The ref is the source of truth for the count; the state mirror is updated
    // inside poll() when it actually changes. Resetting the state value here too
    // would be a synchronous setState in an effect, which starts an extra render
    // for no benefit.
    attemptsRef.current = 0;

    async function poll(): Promise<void> {
      try {
        const status = await api.getQuote(quoteId!, controller.signal);

        if (cancelled) {
          return;
        }

        attemptsRef.current += 1;
        setAttempts(attemptsRef.current);
        setQuote(status);
        setIsLoading(false);

        // --- Terminal: stop polling. This is the condition the instruction
        //     document calls out, and it is driven by the server's own
        //     `isTerminal` flag rather than the client re-deriving which
        //     statuses are final.
        if (status.isTerminal) {
          if (status.offerCount > 0) {
            try {
              const offers = await api.getQuoteOffers(quoteId!, controller.signal);
              if (!cancelled) {
                setComparison(offers);
              }
            } catch (offersError) {
              // The quote status is the important result and it succeeded. A
              // failed comparison fetch should not present as a failed quote.
              if (!cancelled && !(offersError instanceof DOMException)) {
                setError(offersError as Error);
              }
            }
          }

          return;
        }

        if (attemptsRef.current >= MAX_POLL_ATTEMPTS) {
          setTimedOut(true);
          return;
        }

        timer = setTimeout(poll, POLL_INTERVAL_MS);
      } catch (pollError) {
        if (cancelled || pollError instanceof DOMException) {
          // Aborted by teardown, not a real failure.
          return;
        }

        setIsLoading(false);

        // A retryable failure (429, 503, 5xx) during polling is probably a
        // restarting container, so keep trying rather than stranding the user on
        // an error screen. A 404 or 400 is permanent and stops immediately.
        if (pollError instanceof ApiError && pollError.isRetryable) {
          attemptsRef.current += 1;
          setAttempts(attemptsRef.current);

          if (attemptsRef.current < MAX_POLL_ATTEMPTS) {
            timer = setTimeout(poll, POLL_INTERVAL_MS * 2);
            return;
          }

          setTimedOut(true);
        }

        setError(pollError as Error);
      }
    }

    void poll();

    return () => {
      cancelled = true;
      controller.abort();
      if (timer) {
        clearTimeout(timer);
      }
    };
  }, [quoteId, refreshToken]);

  return {
    quote,
    comparison,
    // Derived, so a missing quote id can never present as a stuck spinner.
    isLoading: Boolean(quoteId) && isLoading,
    // Derived rather than stored, so it cannot disagree with the quote state.
    isPolling: Boolean(quote) && !quote!.isTerminal && !timedOut && !error,
    error,
    timedOut,
    attempts,
    refresh,
  };
}
