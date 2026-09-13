import { useState } from 'react';
import {
  ApiError,
  formatCurrency,
  formatDateTime,
  formatNumber,
  formatStatusLabel,
} from '../api/client';
import type { ReviewDecision, UnderwritingReview } from '../api/types';
import { ErrorAlert } from './Alert';
import { NOTE_MAX_LENGTH, validateNote } from './reviewNoteRules';
import { StatusPill } from './StatusPill';

const NOTE_LABELS: Record<ReviewDecision, string> = {
  Approve: 'Underwriting note (optional)',
  FollowUp: 'Information needed from the applicant',
  Decline: 'Reason for declining',
};

const SUBMIT_LABELS: Record<ReviewDecision, string> = {
  Approve: 'Approve quote',
  FollowUp: 'Send follow-up request',
  Decline: 'Decline quote',
};

interface QuoteReviewProps {
  review: UnderwritingReview;
  isSubmitting: boolean;
  error: ApiError | Error | null;
  /** The note that the failed decision was sent with. */
  rejectedNote: string | null;
  onDecide: (decision: ReviewDecision, note: string) => void;
}

/**
 * The review form: the application as submitted, why it was referred, and the
 * decision.
 *
 * The parent renders this with `key={quoteId + updatedUtc}`, so a different quote
 * — or this quote after a decision — mounts a fresh component with a clean form.
 * That is the React way to reset local state; there is no effect watching the
 * review to clear fields by hand.
 */
export function QuoteReview({ review, isSubmitting, error, rejectedNote, onDecide }: QuoteReviewProps) {
  const [decision, setDecision] = useState<ReviewDecision | null>(null);
  const [note, setNote] = useState('');
  const [attempted, setAttempted] = useState(false);

  const willingCarriers = review.carrierAppetite.filter((carrier) => carrier.willWrite);
  const canApprove = willingCarriers.length > 0;

  // Derived, not stored: the server's message about the note applies only while
  // the note is still the text it was about. Editing it clears the message with
  // no effect and no extra state.
  const serverNoteError =
    error instanceof ApiError && rejectedNote !== null && note.trim() === rejectedNote
      ? error.fieldErrors.note
      : undefined;

  const clientNoteError = validateNote(decision, note);
  const noteError = serverNoteError ?? (attempted ? clientNoteError : undefined);
  const decisionMissing = attempted && decision === null;

  const onlyNoteFieldError =
    error instanceof ApiError &&
    Object.keys(error.fieldErrors).length === 1 &&
    Boolean(error.fieldErrors.note);
  const generalError = error && !onlyNoteFieldError ? error : null;

  function handleSubmit(event: React.FormEvent) {
    event.preventDefault();
    setAttempted(true);

    if (decision === null) {
      document.getElementById(`decision-${canApprove ? 'Approve' : 'FollowUp'}`)?.focus();
      return;
    }

    if (clientNoteError) {
      document.getElementById('review-note')?.focus();
      return;
    }

    if (isSubmitting || (decision === 'Approve' && !canApprove)) {
      return;
    }

    onDecide(decision, note.trim());
  }

  const decisionOptions: { value: ReviewDecision; label: string; description: string; disabled: boolean }[] = [
    {
      value: 'Approve',
      label: 'Approve',
      description: canApprove
        ? `Price with the ${willingCarriers.length === 1 ? '1 carrier' : `${willingCarriers.length} carriers`} in appetite and release the offers to the applicant.`
        : 'Unavailable: no carrier on the panel will write this risk.',
      disabled: !canApprove,
    },
    {
      value: 'FollowUp',
      label: 'Request follow-up',
      description: 'Keep it in the queue and tell the applicant what is needed.',
      disabled: false,
    },
    {
      value: 'Decline',
      label: 'Decline',
      description: 'Reject the application. This closes the referral.',
      disabled: false,
    },
  ];

  return (
    <article className="card" aria-labelledby="review-heading">
      <div className="card__header">
        <h2 id="review-heading" style={{ fontSize: 'var(--text-lg)' }}>
          Quote <span className="mono">{review.quoteNumber}</span>
        </h2>
        <StatusPill status={review.status} label={formatStatusLabel(review.status)} />
      </div>

      <form className="card__body stack stack--lg" onSubmit={handleSubmit} noValidate>
        <p className="text-sm text-muted">
          Submitted {formatDateTime(review.submittedUtc)} through the{' '}
          {review.channel === 'LegacyWebForms' ? 'legacy agent portal' : 'online quote flow'}.
        </p>

        {/* ===== The application, read-only ===== */}
        <fieldset className="fieldset">
          <legend className="fieldset__legend">Applicant</legend>
          <div className="form-grid">
            <ReadOnlyField id="review-applicant" label="Insured name" value={review.customerName} />
            <ReadOnlyField id="review-state" label="Garaging state" value={review.state} />
          </div>
        </fieldset>

        <fieldset className="fieldset">
          <legend className="fieldset__legend">Vehicle</legend>
          <div className="form-grid">
            <ReadOnlyField id="review-year" label="Year" value={String(review.vehicleYear)} />
            <ReadOnlyField id="review-make" label="Make" value={review.vehicleMake} />
            <ReadOnlyField id="review-model" label="Model" value={review.vehicleModel} />
            <ReadOnlyField id="review-mileage" label="Annual mileage" value={formatNumber(review.annualMileage)} />
          </div>
        </fieldset>

        <fieldset className="fieldset stack">
          <legend className="fieldset__legend">Coverage history and house rules</legend>
          <div className="form-grid">
            <ReadOnlyField id="review-prior" label="Prior insurance (months)" value={String(review.priorInsuranceMonths)} />
            <ReadOnlyField id="review-eligibility" label="Eligibility result" value={review.eligibilityOutcome} />
            <ReadOnlyField
              id="review-baseline"
              label="House baseline premium"
              value={
                review.baselineAnnualPremium === undefined
                  ? 'Not priced'
                  : formatCurrency(review.baselineAnnualPremium)
              }
            />
          </div>

          {review.decisionReason && (
            <div className={`alert alert--${review.isAwaitingReview ? 'warning' : 'info'}`}>
              <p>
                <strong>
                  {review.status === 'FollowUp'
                    ? 'Follow-up requested:'
                    : review.status === 'Declined'
                      ? 'Declined:'
                      : 'Referred:'}
                </strong>{' '}
                {review.decisionReason}
              </p>
            </div>
          )}

          <details open className="text-sm">
            <summary style={{ cursor: 'pointer', fontWeight: 600 }}>
              Rules applied at submission ({review.eligibilityTrace.length})
            </summary>
            <ol className="mono text-xs" style={{ margin: 'var(--space-2) 0 0', paddingLeft: 'var(--space-5)' }}>
              {review.eligibilityTrace.map((line, index) => (
                <li key={index}>{line}</li>
              ))}
            </ol>
          </details>
        </fieldset>

        <fieldset className="fieldset stack stack--sm">
          <legend className="fieldset__legend">Carrier appetite</legend>
          <p className="text-sm">
            <strong>
              {willingCarriers.length} of {review.carrierAppetite.length}
            </strong>{' '}
            carriers would price this risk if it is approved.
          </p>
          <ul className="appetite-list">
            {review.carrierAppetite.map((carrier) => (
              <li
                key={carrier.carrierId}
                className={`appetite-item${carrier.willWrite ? '' : ' appetite-item--declined'}`}
              >
                <span className="appetite-item__mark" aria-hidden="true">
                  {carrier.willWrite ? '✓' : '✕'}
                </span>
                <span>
                  <strong>{carrier.carrierName}</strong>
                  <span className="sr-only">{carrier.willWrite ? ' will write' : ' will not write'}</span>
                  {carrier.reason && <span className="appetite-item__reason">{carrier.reason}</span>}
                </span>
              </li>
            ))}
          </ul>
        </fieldset>

        {review.reviewedBy && (
          <fieldset className="fieldset">
            <legend className="fieldset__legend">Last underwriter action</legend>
            <p className="text-sm">
              <strong>{review.reviewedBy}</strong>
              {review.reviewedUtc && <> on {formatDateTime(review.reviewedUtc)}</>}
            </p>
            {review.reviewNote && (
              <p className="text-sm" style={{ marginTop: 'var(--space-1)', fontStyle: 'italic' }}>
                “{review.reviewNote}”
              </p>
            )}
          </fieldset>
        )}

        {/* Outside the decision block: after a 409 the quote is reloaded in its
            decided state and the form disappears, but the underwriter still needs
            to know why their decision was not recorded. */}
        {generalError && <ErrorAlert error={generalError} />}

        {review.isAwaitingReview ? (
          <fieldset className="fieldset decision-panel stack">
            <legend className="fieldset__legend">Decision</legend>

            <fieldset
              className="decision-options"
              aria-describedby={decisionMissing ? 'decision-error' : undefined}
            >
              <legend className="sr-only">Choose a decision</legend>
              {decisionOptions.map((option) => (
                <label
                  key={option.value}
                  className={[
                    'decision-option',
                    decision === option.value ? 'decision-option--chosen' : '',
                    option.disabled ? 'decision-option--disabled' : '',
                  ]
                    .filter(Boolean)
                    .join(' ')}
                >
                  <input
                    type="radio"
                    id={`decision-${option.value}`}
                    name="decision"
                    value={option.value}
                    checked={decision === option.value}
                    disabled={option.disabled}
                    onChange={() => setDecision(option.value)}
                  />
                  <span>
                    <span className="decision-option__label">{option.label}</span>
                    <span className="decision-option__description">{option.description}</span>
                  </span>
                </label>
              ))}
            </fieldset>
            {decisionMissing && (
              <p className="form-field__error" id="decision-error" role="alert">
                Choose a decision.
              </p>
            )}

            <div className="form-field">
              <label className="form-field__label" htmlFor="review-note">
                {decision ? NOTE_LABELS[decision] : NOTE_LABELS.Approve}
              </label>
              <textarea
                id="review-note"
                className="input"
                rows={4}
                maxLength={NOTE_MAX_LENGTH}
                value={note}
                onChange={(event) => setNote(event.target.value)}
                aria-invalid={Boolean(noteError)}
                aria-describedby={noteError ? 'review-note-count review-note-error' : 'review-note-count'}
                style={{ resize: 'vertical' }}
              />
              <span id="review-note-count" className="form-field__hint" style={{ alignSelf: 'flex-end' }}>
                {note.length} / {NOTE_MAX_LENGTH}
              </span>
              {noteError && (
                <p className="form-field__error" id="review-note-error" role="alert">
                  {noteError}
                </p>
              )}
            </div>

            <div className="row" style={{ justifyContent: 'flex-end' }}>
              <button
                type="submit"
                className={`btn ${decision === 'Decline' ? 'btn--accent' : 'btn--primary'}`}
                disabled={isSubmitting}
              >
                {isSubmitting ? (
                  <>
                    <span className="spinner" aria-hidden="true" />
                    Saving…
                  </>
                ) : decision ? (
                  SUBMIT_LABELS[decision]
                ) : (
                  'Record decision'
                )}
              </button>
            </div>
          </fieldset>
        ) : (
          <div className="alert alert--success" role="status">
            <p>
              {review.status === 'Rated' ? (
                <>
                  <strong>Approved{review.reviewedBy ? ` by ${review.reviewedBy}` : ''}.</strong>{' '}
                  {review.offerCount} carrier offers are available to the applicant; best annual premium{' '}
                  {formatCurrency(review.annualPremium)}.
                </>
              ) : review.status === 'Declined' ? (
                <>
                  <strong>Declined{review.reviewedBy ? ` by ${review.reviewedBy}` : ''}.</strong> This referral
                  is closed.
                </>
              ) : (
                <>
                  <strong>No decision is needed.</strong> This quote is {review.status}.
                </>
              )}
            </p>
          </div>
        )}
      </form>
    </article>
  );
}

function ReadOnlyField({ id, label, value }: { id: string; label: string; value: string }) {
  return (
    <div className="form-field">
      <label className="form-field__label" htmlFor={id}>
        {label}
      </label>
      <input id={id} className="input input--readonly" value={value} readOnly />
    </div>
  );
}
