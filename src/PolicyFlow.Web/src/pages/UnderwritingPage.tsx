import { useEffect, useState } from 'react';
import { Alert, ErrorAlert } from '../components/Alert';
import { QuoteReview } from '../components/QuoteReview';
import { WorkQueue } from '../components/WorkQueue';
import { useUnderwritingWorkbench } from '../hooks/useUnderwritingWorkbench';

/**
 * Underwriter work queue: pending referrals beside the review form.
 *
 * The same screen as the Angular module the legacy portal hosts in
 * `Underwriting.aspx`, against the same `/api/underwriting` endpoints, so the two
 * can be compared side by side. Neither holds a business rule: the eligibility
 * trace, carrier appetite and decision rules all come from the API.
 */
export function UnderwritingPage() {
  const workbench = useUnderwritingWorkbench();
  const { queue, review, loadQueue, select, decide } = workbench;

  // Ticks once a minute so "time in queue" stays current without refetching.
  const [now, setNow] = useState(() => Date.now());

  useEffect(() => {
    const timer = setInterval(() => setNow(Date.now()), 60_000);
    return () => clearInterval(timer);
  }, []);

  useEffect(() => {
    void loadQueue();
  }, [loadQueue]);

  return (
    <div className="stack stack--lg">
      <div className="row row--between" style={{ alignItems: 'flex-end' }}>
        <div>
          <h1>Underwriting work queue</h1>
          <p className="text-muted" style={{ marginTop: 'var(--space-2)' }}>
            Referred applications awaiting a licensed underwriter's decision.
          </p>
        </div>

        {queue && (
          <dl className="workbench-stats">
            <div>
              <dt>Referrals</dt>
              <dd>{queue.referralCount}</dd>
            </div>
            <div>
              <dt>Follow-up</dt>
              <dd>{queue.followUpCount}</dd>
            </div>
          </dl>
        )}
      </div>

      {/* Always rendered, so the outcome is announced when it appears. */}
      <div role="status" aria-live="polite" className="workbench-live">
        {workbench.lastOutcome && (
          <div className="alert alert--success row row--between">
            <span>{workbench.lastOutcome.message}</span>
            <button type="button" className="btn btn--secondary btn--sm" onClick={workbench.dismissOutcome}>
              Dismiss
            </button>
          </div>
        )}
      </div>

      {workbench.queueError && <ErrorAlert error={workbench.queueError} onRetry={() => void loadQueue()} />}

      {queue?.isTruncated && (
        <Alert variant="info">Showing the oldest quotes only. Clear them to see the rest of the queue.</Alert>
      )}

      <div className="workbench">
        <WorkQueue
          items={queue?.items ?? []}
          selectedQuoteId={workbench.selectedQuoteId}
          isLoading={workbench.isQueueLoading}
          now={now}
          onSelect={(quoteId) => void select(quoteId)}
          onRefresh={() => void loadQueue()}
        />

        <div>
          {workbench.isReviewLoading ? (
            <div className="card workbench-placeholder">Loading quote…</div>
          ) : workbench.reviewError ? (
            <ErrorAlert error={workbench.reviewError} />
          ) : review ? (
            <QuoteReview
              // A new quote, or this quote after a decision, remounts the form
              // with clean state.
              key={`${review.quoteId}:${review.updatedUtc}`}
              review={review}
              isSubmitting={workbench.isSubmitting}
              error={workbench.decisionError}
              rejectedNote={workbench.rejectedNote}
              onDecide={(decision, note) => void decide(decision, note)}
            />
          ) : (
            <div className="card workbench-placeholder">
              Select a quote from the work queue to review the application.
            </div>
          )}
        </div>
      </div>

      <p className="text-xs text-muted">
        The same work queue runs as an Angular module inside the legacy Web Forms portal
        (Underwriting.aspx), against the same API.
      </p>
    </div>
  );
}
