import type { PolicyStatus, QuoteStatus } from '../api/types';

interface StatusPillProps {
  status: QuoteStatus | PolicyStatus | string;
  /** Display text when it should differ from the raw status, e.g. "Follow-up". */
  label?: string;
}

/**
 * Colour-coded status badge.
 *
 * Colour alone is never the only signal: the status word is always present, so
 * the component works for colour-blind users and in a monochrome print.
 */
export function StatusPill({ status, label }: StatusPillProps) {
  const modifier = status.toLowerCase();
  const isInFlight = modifier === 'pending' || modifier === 'processing';

  return (
    <span className={`pill pill--${modifier}`}>
      <span className="pill__dot" aria-hidden="true" />
      {label ?? status}
      {isInFlight && <span className="sr-only"> — underwriting in progress</span>}
    </span>
  );
}

interface QuoteTimelineProps {
  status: QuoteStatus;
}

/**
 * Visualises the quote lifecycle.
 *
 * Makes the asynchronous pipeline legible: the customer sees that Pending and
 * Processing are real, expected stages rather than the page being broken. That is
 * the user-facing justification for returning 202 instead of blocking.
 */
export function QuoteTimeline({ status }: QuoteTimelineProps) {
  // The terminal step is labelled with the outcome actually reached, so a
  // referral or decline is not shown as a failure to become "Rated".
  const terminalLabel =
    status === 'Referral'
      ? 'Referred'
      : status === 'Declined'
        ? 'Declined'
        : status === 'FollowUp'
          ? 'Info requested'
          : 'Rated';

  const steps = [
    { key: 'submitted', label: 'Submitted' },
    { key: 'underwriting', label: 'Underwriting' },
    { key: 'terminal', label: terminalLabel },
  ];

  const currentIndex =
    status === 'Pending' ? 0 : status === 'Processing' ? 1 : 2;

  const isTerminal =
    status === 'Rated' ||
    status === 'Referral' ||
    status === 'Declined' ||
    status === 'FollowUp';

  return (
    <ol className="timeline" aria-label="Quote progress">
      {steps.map((step, index) => {
        const isDone = index < currentIndex || (index === currentIndex && isTerminal);
        const isCurrent = index === currentIndex && !isTerminal;

        const className = [
          'timeline__step',
          isDone ? 'timeline__step--done' : '',
          isCurrent ? 'timeline__step--current' : '',
        ]
          .filter(Boolean)
          .join(' ');

        return (
          <li key={step.key} className={className} aria-current={isCurrent ? 'step' : undefined}>
            <span className="timeline__marker" aria-hidden="true">
              {isDone ? '✓' : index + 1}
            </span>
            {step.label}
          </li>
        );
      })}
    </ol>
  );
}
