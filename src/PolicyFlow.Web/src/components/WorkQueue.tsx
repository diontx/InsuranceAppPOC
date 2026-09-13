import { useMemo, useState } from 'react';
import { formatStatusLabel, formatTimeInQueue } from '../api/client';
import type { QueueStatus, UnderwritingQueueItem } from '../api/types';
import { StatusPill } from './StatusPill';

type QueueFilter = 'All' | QueueStatus;

const FILTERS: { value: QueueFilter; label: string }[] = [
  { value: 'All', label: 'All' },
  { value: 'Referral', label: 'Referrals' },
  { value: 'FollowUp', label: 'Follow-up' },
];

interface WorkQueueProps {
  items: UnderwritingQueueItem[];
  selectedQuoteId: string | null;
  isLoading: boolean;
  /** Epoch milliseconds, ticked by the page so ages refresh. */
  now: number;
  onSelect: (quoteId: string) => void;
  onRefresh: () => void;
}

/**
 * The list of quotes awaiting an underwriter.
 *
 * Filter and search are local state: nothing else on the page cares what the
 * underwriter typed. Derived with `useMemo` rather than stored, so the visible
 * list can never disagree with the items it was filtered from.
 */
export function WorkQueue({ items, selectedQuoteId, isLoading, now, onSelect, onRefresh }: WorkQueueProps) {
  const [filter, setFilter] = useState<QueueFilter>('All');
  const [search, setSearch] = useState('');

  const counts = useMemo<Record<QueueFilter, number>>(
    () => ({
      All: items.length,
      Referral: items.filter((item) => item.status === 'Referral').length,
      FollowUp: items.filter((item) => item.status === 'FollowUp').length,
    }),
    [items],
  );

  const visibleItems = useMemo(() => {
    const term = search.trim().toLowerCase();

    return items.filter(
      (item) =>
        (filter === 'All' || item.status === filter) &&
        (term.length === 0 ||
          item.quoteNumber.toLowerCase().includes(term) ||
          item.customerName.toLowerCase().includes(term)),
    );
  }, [items, filter, search]);

  let emptyMessage = 'No quotes are awaiting review.';
  if (items.length > 0) {
    emptyMessage = 'No quotes match this filter.';
  } else if (isLoading) {
    emptyMessage = 'Loading the queue…';
  }

  return (
    <section className="card workbench__queue" aria-labelledby="queue-heading">
      <div className="card__header">
        <h2 id="queue-heading" style={{ fontSize: 'var(--text-base)' }}>
          Work queue
        </h2>
        <button type="button" className="btn btn--secondary btn--sm" onClick={onRefresh} disabled={isLoading}>
          {isLoading ? 'Refreshing…' : 'Refresh'}
        </button>
      </div>

      <div className="queue-tools">
        <div className="segmented" role="group" aria-label="Filter by status">
          {FILTERS.map((option) => (
            <button
              key={option.value}
              type="button"
              className="segmented__option"
              aria-pressed={filter === option.value}
              onClick={() => setFilter(option.value)}
            >
              {option.label} <span className="text-muted">{counts[option.value]}</span>
            </button>
          ))}
        </div>

        <label className="sr-only" htmlFor="queue-search">
          Search by quote number or applicant
        </label>
        <input
          id="queue-search"
          type="search"
          className="input"
          placeholder="Quote # or applicant"
          autoComplete="off"
          value={search}
          onChange={(event) => setSearch(event.target.value)}
        />
      </div>

      {visibleItems.length === 0 ? (
        <p className="queue-empty text-muted">{emptyMessage}</p>
      ) : (
        <ul className="queue-list">
          {visibleItems.map((item) => (
            <li key={item.quoteId}>
              <button
                type="button"
                className="queue-item"
                aria-current={item.quoteId === selectedQuoteId ? 'true' : undefined}
                data-quote-number={item.quoteNumber}
                onClick={() => onSelect(item.quoteId)}
              >
                <span className="queue-item__row">
                  <span className="mono queue-item__number">{item.quoteNumber}</span>
                  <StatusPill status={item.status} label={formatStatusLabel(item.status)} />
                </span>
                <span className="queue-item__name">{item.customerName}</span>
                <span className="queue-item__meta">
                  {item.state} · {item.vehicleDescription}
                </span>
                {item.reason && <span className="queue-item__reason">{item.reason}</span>}
                <span className="queue-item__meta">In queue {formatTimeInQueue(item.submittedUtc, now)}</span>
              </button>
            </li>
          ))}
        </ul>
      )}
    </section>
  );
}
