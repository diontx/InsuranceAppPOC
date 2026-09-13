import { useCallback, useEffect, useRef, useState } from 'react';
import { ApiError, api, formatCurrency } from '../api/client';
import type { ReviewDecision, UnderwritingQueue, UnderwritingReview } from '../api/types';

/** A plain-language confirmation of what a decision did. */
export interface DecisionOutcome {
  quoteNumber: string;
  decision: ReviewDecision;
  message: string;
}

export interface UnderwritingWorkbench {
  queue: UnderwritingQueue | null;
  isQueueLoading: boolean;
  queueError: ApiError | Error | null;
  selectedQuoteId: string | null;
  review: UnderwritingReview | null;
  isReviewLoading: boolean;
  reviewError: ApiError | Error | null;
  isSubmitting: boolean;
  decisionError: ApiError | Error | null;
  /**
   * The note sent with the decision that failed. Lets the form show the server's
   * message about the note only while the note is still the one it was about.
   */
  rejectedNote: string | null;
  lastOutcome: DecisionOutcome | null;
  loadQueue: () => Promise<void>;
  select: (quoteId: string) => Promise<void>;
  /** Resolves true when the decision was saved. */
  decide: (decision: ReviewDecision, note: string) => Promise<boolean>;
  dismissOutcome: () => void;
}

/**
 * State and actions for the underwriting work queue.
 *
 * The React counterpart of the Angular module's `UnderwritingStore`: one owner for
 * the queue, the open quote and the decision in flight, so components only render
 * and report intent. A hook scoped to the page rather than a global store, because
 * nothing outside this page needs any of it.
 */
export function useUnderwritingWorkbench(): UnderwritingWorkbench {
  const [queue, setQueue] = useState<UnderwritingQueue | null>(null);
  const [isQueueLoading, setIsQueueLoading] = useState(false);
  const [queueError, setQueueError] = useState<ApiError | Error | null>(null);
  const [selectedQuoteId, setSelectedQuoteId] = useState<string | null>(null);
  const [review, setReview] = useState<UnderwritingReview | null>(null);
  const [isReviewLoading, setIsReviewLoading] = useState(false);
  const [reviewError, setReviewError] = useState<ApiError | Error | null>(null);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [decisionError, setDecisionError] = useState<ApiError | Error | null>(null);
  const [rejectedNote, setRejectedNote] = useState<string | null>(null);
  const [lastOutcome, setLastOutcome] = useState<DecisionOutcome | null>(null);

  /**
   * Incremented on every selection. A review response is applied only if it
   * belongs to the latest selection: an underwriter who clicks A then B must end
   * up looking at B even when A's response arrives last. The AbortController
   * cancels A's request too, but a response can already be in the microtask queue
   * when abort runs, so the version check is the actual guarantee.
   */
  const selectionVersion = useRef(0);
  const reviewController = useRef<AbortController | null>(null);

  /**
   * Refs, not state, for values read inside async callbacks. State read there is
   * the value from the render that created the callback, so a double-click inside
   * one render would see `isSubmitting === false` twice.
   */
  const currentReview = useRef<UnderwritingReview | null>(null);
  const submitting = useRef(false);
  const mounted = useRef(false);

  useEffect(() => {
    mounted.current = true;
    return () => {
      mounted.current = false;
      reviewController.current?.abort();
    };
  }, []);

  const applyReview = useCallback((next: UnderwritingReview | null) => {
    currentReview.current = next;
    setReview(next);
  }, []);

  const loadQueue = useCallback(async () => {
    setIsQueueLoading(true);
    setQueueError(null);

    try {
      const result = await api.getUnderwritingQueue();
      if (mounted.current) {
        setQueue(result);
      }
    } catch (error) {
      if (mounted.current) {
        setQueueError(error as Error);
      }
    } finally {
      if (mounted.current) {
        setIsQueueLoading(false);
      }
    }
  }, []);

  const select = useCallback(
    async (quoteId: string) => {
      const version = ++selectionVersion.current;

      reviewController.current?.abort();
      const controller = new AbortController();
      reviewController.current = controller;

      setSelectedQuoteId(quoteId);
      applyReview(null);
      setReviewError(null);
      setDecisionError(null);
      setRejectedNote(null);
      setIsReviewLoading(true);

      const isCurrent = () => mounted.current && version === selectionVersion.current;

      try {
        const result = await api.getUnderwritingReview(quoteId, controller.signal);
        if (isCurrent()) {
          applyReview(result);
        }
      } catch (error) {
        // An abort is this hook cancelling a stale request, not a failure.
        if (isCurrent() && !(error instanceof DOMException)) {
          setReviewError(error as Error);
        }
      } finally {
        if (isCurrent()) {
          setIsReviewLoading(false);
        }
      }
    },
    [applyReview],
  );

  const decide = useCallback(
    async (decision: ReviewDecision, note: string) => {
      const current = currentReview.current;

      // One decision per click. The API would answer a second one with a 409, but
      // an underwriter should never see a conflict with their own double-click.
      if (!current || submitting.current) {
        return false;
      }

      const version = selectionVersion.current;
      const trimmed = note.trim();

      submitting.current = true;
      setIsSubmitting(true);
      setDecisionError(null);
      setRejectedNote(null);
      setLastOutcome(null);

      try {
        const updated = await api.decideReferral(current.quoteId, {
          decision,
          note: trimmed.length > 0 ? trimmed : undefined,
        });

        if (!mounted.current) {
          return true;
        }

        if (version === selectionVersion.current) {
          applyReview(updated);
        }

        setLastOutcome(describeOutcome(updated, decision));
        await loadQueue();
        return true;
      } catch (error) {
        if (!mounted.current) {
          return false;
        }

        setDecisionError(error as Error);
        setRejectedNote(trimmed);

        if (error instanceof ApiError && error.status === 409) {
          // Someone else decided first. Show what the quote is now, and a queue
          // without it, rather than a form that can only fail again.
          await Promise.all([loadQueue(), refreshReview(current.quoteId)]);
        }

        return false;
      } finally {
        submitting.current = false;
        if (mounted.current) {
          setIsSubmitting(false);
        }
      }

      async function refreshReview(quoteId: string) {
        try {
          const latest = await api.getUnderwritingReview(quoteId);
          if (mounted.current && version === selectionVersion.current) {
            applyReview(latest);
          }
        } catch {
          // The decision error is already on screen; a failed refresh adds nothing.
        }
      }
    },
    [applyReview, loadQueue],
  );

  const dismissOutcome = useCallback(() => setLastOutcome(null), []);

  return {
    queue,
    isQueueLoading,
    queueError,
    selectedQuoteId,
    review,
    isReviewLoading,
    reviewError,
    isSubmitting,
    decisionError,
    rejectedNote,
    lastOutcome,
    loadQueue,
    select,
    decide,
    dismissOutcome,
  };
}

/** The confirmation shown after a decision, in the same words as the Angular module. */
export function describeOutcome(review: UnderwritingReview, decision: ReviewDecision): DecisionOutcome {
  const { quoteNumber } = review;

  switch (decision) {
    case 'Approve': {
      const offers = review.offerCount === 1 ? '1 carrier offer' : `${review.offerCount} carrier offers`;
      return {
        quoteNumber,
        decision,
        message: `${quoteNumber} approved and rated: ${offers}, best annual premium ${formatCurrency(review.annualPremium)}.`,
      };
    }
    case 'FollowUp':
      return {
        quoteNumber,
        decision,
        message: `${quoteNumber} is held for follow-up. The applicant has been asked: "${review.decisionReason ?? ''}"`,
      };
    case 'Decline':
      return { quoteNumber, decision, message: `${quoteNumber} declined.` };
  }
}
