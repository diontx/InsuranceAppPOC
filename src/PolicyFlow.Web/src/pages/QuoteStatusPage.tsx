import { useMemo, useState } from 'react';
import { useNavigate, useParams } from 'react-router-dom';
import { ApiError, api, formatCurrency } from '../api/client';
import type { CoverageTier } from '../api/types';
import { Alert, ErrorAlert } from '../components/Alert';
import { OfferCard } from '../components/OfferCard';
import { QuoteTimeline, StatusPill } from '../components/StatusPill';
import { useQuotePolling } from '../hooks/useQuotePolling';

type TierFilter = 'All' | CoverageTier;

const TIER_FILTERS: TierFilter[] = ['All', 'Basic', 'Standard', 'Premium'];

/**
 * Quote status and carrier comparison.
 *
 * Polls until underwriting reaches a terminal state, then renders the panel. The
 * progression Pending → Processing → Rated is shown explicitly, because that
 * visible sequence is what makes the asynchronous design legible instead of
 * looking like a slow page.
 */
export function QuoteStatusPage() {
  const { quoteId } = useParams<{ quoteId: string }>();
  const navigate = useNavigate();
  const { quote, comparison, isLoading, isPolling, error, timedOut, attempts, refresh } =
    useQuotePolling(quoteId);

  const [tierFilter, setTierFilter] = useState<TierFilter>('All');
  const [selectedOfferId, setSelectedOfferId] = useState<string | null>(null);
  const [bindingOfferId, setBindingOfferId] = useState<string | null>(null);
  const [bindError, setBindError] = useState<Error | ApiError | null>(null);

  const visibleOffers = useMemo(() => {
    if (!comparison) {
      return [];
    }

    return tierFilter === 'All'
      ? comparison.offers
      : comparison.offers.filter((offer) => offer.coverageTier === tierFilter);
  }, [comparison, tierFilter]);

  async function handleBind(offerId: string) {
    if (!quoteId) {
      return;
    }

    setBindingOfferId(offerId);
    setBindError(null);

    try {
      const policy = await api.createPolicy(quoteId, offerId);
      navigate(`/policies/${policy.policyId}`);
    } catch (bindingError) {
      setBindError(bindingError as Error);
    } finally {
      setBindingOfferId(null);
    }
  }

  if (isLoading) {
    return (
      <div className="card">
        <div className="card__body row">
          <span className="spinner" aria-hidden="true" />
          <p>Loading your quote…</p>
        </div>
      </div>
    );
  }

  if (error && !quote) {
    return <ErrorAlert error={error} onRetry={refresh} />;
  }

  if (!quote) {
    return (
      <Alert variant="danger" title="Quote not found">
        No quote exists with that identifier.
      </Alert>
    );
  }

  return (
    <div className="stack stack--lg">
      {/* --- Header ---------------------------------------------------- */}
      <section className="card">
        <div className="card__header">
          <div>
            <h1 style={{ fontSize: 'var(--text-xl)' }}>Quote {quote.quoteNumber}</h1>
            <p className="text-sm text-muted" style={{ marginTop: 'var(--space-1)' }}>
              {quote.customerName} · {quote.vehicleDescription} · {quote.state}
            </p>
          </div>
          <StatusPill status={quote.status} />
        </div>

        <div className="card__body stack stack--lg">
          <QuoteTimeline status={quote.status} />

          {/* A live region: a screen reader announces each status change without
              the user having to re-read the page. */}
          <p className="sr-only" role="status" aria-live="polite">
            Quote status: {quote.status}
            {quote.annualPremium !== undefined &&
              `. Best premium ${formatCurrency(quote.annualPremium)} per year.`}
          </p>

          {isPolling && (
            <div className="row text-sm text-muted">
              <span className="spinner" aria-hidden="true" />
              <span>
                Shopping your risk against the carrier panel… (check {attempts})
              </span>
            </div>
          )}

          {timedOut && (
            <Alert variant="warning" title="Still working">
              <p>
                Underwriting has not finished after {attempts} checks. The background
                worker may be restarting.
              </p>
              <button
                type="button"
                className="btn btn--secondary btn--sm"
                onClick={refresh}
                style={{ marginTop: 'var(--space-3)' }}
              >
                Check again
              </button>
            </Alert>
          )}

          <dl className="summary-list summary-list--wide">
            <div className="summary-item">
              <dt>Best annual premium</dt>
              <dd>
                {quote.annualPremium !== undefined
                  ? formatCurrency(quote.annualPremium)
                  : '—'}
              </dd>
            </div>
            <div className="summary-item">
              <dt>Options found</dt>
              <dd>{quote.offerCount}</dd>
            </div>
            <div className="summary-item">
              <dt>Annual mileage</dt>
              <dd>{quote.annualMileage.toLocaleString('en-US')}</dd>
            </div>
            <div className="summary-item">
              <dt>Prior coverage</dt>
              <dd>{quote.priorInsuranceMonths} months</dd>
            </div>
          </dl>
        </div>

        <div className="card__footer">
          <p className="text-xs text-muted mono">
            Correlation id: {quote.correlationId}
          </p>
          <p className="text-xs text-muted" style={{ marginTop: 'var(--space-1)' }}>
            This single id traces the quote through the API, SQL Server, the Service
            Bus message and the underwriting worker.
            {quote.channel === 'LegacyWebForms' &&
              ' This quote arrived through the legacy Web Forms channel.'}
          </p>
        </div>
      </section>

      {/* --- Referral / decline ---------------------------------------- */}
      {quote.status === 'Referral' && (
        <Alert variant="warning" title="Referred for underwriter review">
          <p>{quote.decisionReason}</p>
          <p style={{ marginTop: 'var(--space-2)' }}>
            No automated pricing is available. A licensed underwriter will review this
            application.
          </p>
        </Alert>
      )}

      {quote.status === 'FollowUp' && (
        <Alert variant="warning" title="The underwriter needs more information">
          <p>{quote.decisionReason}</p>
          <p style={{ marginTop: 'var(--space-2)' }}>
            Your application is on hold until this is provided. Your agent will be in
            touch.
          </p>
        </Alert>
      )}

      {quote.status === 'Declined' && (
        <Alert variant="danger" title="Not eligible for this program">
          <p>{quote.decisionReason}</p>
        </Alert>
      )}

      {bindError && <ErrorAlert error={bindError} />}

      {/* --- Carrier comparison ---------------------------------------- */}
      {comparison && comparison.offers.length > 0 && (
        <section>
          <div className="section-heading">
            <div>
              <h2>Your options</h2>
              <p className="text-sm text-muted" style={{ marginTop: 'var(--space-1)' }}>
                {comparison.offers.length} priced options from{' '}
                {new Set(comparison.offers.map((o) => o.carrierId)).size} carriers, shopped
                in {comparison.totalRatingMs} ms.
                {comparison.baselineAnnualPremium !== undefined &&
                  ` Reference premium: ${formatCurrency(comparison.baselineAnnualPremium)}.`}
              </p>
            </div>

            <div className="segmented" role="group" aria-label="Filter by coverage tier">
              {TIER_FILTERS.map((tier) => (
                <button
                  key={tier}
                  type="button"
                  className="segmented__option"
                  aria-pressed={tierFilter === tier}
                  onClick={() => setTierFilter(tier)}
                >
                  {tier}
                </button>
              ))}
            </div>
          </div>

          <div className="offer-grid">
            {visibleOffers.map((offer) => (
              <OfferCard
                key={offer.offerId}
                offer={offer}
                baselinePremium={comparison.baselineAnnualPremium}
                isSelected={selectedOfferId === offer.offerId}
                onSelect={setSelectedOfferId}
                onBind={handleBind}
                isBinding={bindingOfferId === offer.offerId}
              />
            ))}
          </div>

          {visibleOffers.length === 0 && (
            <p className="text-sm text-muted">
              No {tierFilter} options were returned for this risk.
            </p>
          )}
        </section>
      )}

      {/* --- Carriers that made no offer -------------------------------- */}
      {comparison && comparison.declinations.length > 0 && (
        <section>
          <div className="section-heading">
            <h2 style={{ fontSize: 'var(--text-lg)' }}>Carriers with no offer</h2>
          </div>

          <ul className="declination-list">
            {comparison.declinations.map((declination) => (
              <li key={declination.carrierId} className="declination">
                <span className="declination__carrier">{declination.carrierName}</span>
                <span className="declination__reason">{declination.reason}</span>
                {declination.wasUnavailable && (
                  <span className="pill pill--declined">Unavailable</span>
                )}
              </li>
            ))}
          </ul>

          <p className="text-xs text-muted" style={{ marginTop: 'var(--space-3)' }}>
            Carriers set their own appetite. A carrier that is not licensed in your
            state, or that will not write your mileage or coverage history, returns
            nothing rather than a price.
          </p>
        </section>
      )}
    </div>
  );
}
