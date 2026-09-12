import { useState } from 'react';
import { Link } from 'react-router-dom';
import { ApiError, api, formatCurrency } from '../api/client';
import type { QuoteLookupResponse } from '../api/types';
import { Alert, ErrorAlert } from '../components/Alert';
import { OfferCard } from '../components/OfferCard';
import { StatusPill } from '../components/StatusPill';

/**
 * Mobile quote lookup.
 *
 * The scenario this is designed for: a customer standing outside a dealership
 * with a text message containing a quote number, on a phone, on cellular, with no
 * session. So:
 *
 * - Two fields only, both `inputMode`-hinted so the phone shows the right keyboard.
 * - One API call returns status *and* the full comparison, because a second round
 *   trip on a slow connection is a visible delay.
 * - `autoCapitalize="characters"` on the quote number, since quote numbers are
 *   uppercase and a phone would otherwise auto-capitalise only the first letter.
 * - Results render as stacked cards, never a wide table.
 *
 * Requires both quote number and surname. Quote numbers are sequential and
 * therefore guessable, so the surname acts as a second factor; a wrong surname
 * returns the same "not found" as a wrong number, which stops the endpoint being
 * used to enumerate valid quotes.
 */
export function LookupPage() {
  const [quoteNumber, setQuoteNumber] = useState('');
  const [lastName, setLastName] = useState('');
  const [result, setResult] = useState<QuoteLookupResponse | null>(null);
  const [error, setError] = useState<Error | ApiError | null>(null);
  const [isSearching, setIsSearching] = useState(false);
  const [notFound, setNotFound] = useState(false);
  const [selectedOfferId, setSelectedOfferId] = useState<string | null>(null);

  async function handleSearch(event: React.FormEvent) {
    event.preventDefault();

    if (!quoteNumber.trim() || !lastName.trim()) {
      return;
    }

    setIsSearching(true);
    setError(null);
    setNotFound(false);
    setResult(null);

    try {
      const found = await api.lookupQuote(quoteNumber.trim(), lastName.trim());
      setResult(found);
    } catch (lookupError) {
      // 404 is an expected outcome here, not an error condition, so it gets a
      // helpful message rather than a red error panel.
      if (lookupError instanceof ApiError && lookupError.status === 404) {
        setNotFound(true);
      } else {
        setError(lookupError as Error);
      }
    } finally {
      setIsSearching(false);
    }
  }

  return (
    <div className="stack stack--lg">
      <div>
        <h1>Find my quote</h1>
        <p className="text-muted" style={{ marginTop: 'var(--space-2)' }}>
          Enter the quote number from your confirmation and the last name on the
          quote.
        </p>
      </div>

      <form className="card" onSubmit={handleSearch} noValidate>
        <div className="card__body stack">
          <div className="form-grid">
            <div className="form-field">
              <label className="form-field__label" htmlFor="quoteNumber">
                Quote number
              </label>
              <input
                id="quoteNumber"
                className="input mono"
                type="text"
                value={quoteNumber}
                onChange={(e) => setQuoteNumber(e.target.value.toUpperCase())}
                placeholder="Q-2026-000001"
                // Quote numbers are uppercase; without this a phone capitalises
                // only the first character and the user fights the keyboard.
                autoCapitalize="characters"
                autoCorrect="off"
                spellCheck={false}
                inputMode="text"
                required
              />
              <p className="form-field__hint">Shown on your confirmation.</p>
            </div>

            <div className="form-field">
              <label className="form-field__label" htmlFor="lastName">
                Last name
              </label>
              <input
                id="lastName"
                className="input"
                type="text"
                value={lastName}
                onChange={(e) => setLastName(e.target.value)}
                placeholder="Johnson"
                autoComplete="family-name"
                required
              />
              <p className="form-field__hint">The surname on the quote.</p>
            </div>
          </div>
        </div>

        <div className="card__footer">
          <button
            type="submit"
            className="btn btn--primary btn--block"
            disabled={isSearching || !quoteNumber.trim() || !lastName.trim()}
          >
            {isSearching ? (
              <>
                <span className="spinner" aria-hidden="true" />
                Searching…
              </>
            ) : (
              'Find my quote'
            )}
          </button>
        </div>
      </form>

      {notFound && (
        <Alert variant="warning" title="No matching quote">
          <p>
            No quote matches that quote number and last name. Check both values and
            try again.
          </p>
        </Alert>
      )}

      {error && <ErrorAlert error={error} />}

      {result && (
        <>
          <section className="card">
            <div className="card__header">
              <div>
                <h2 style={{ fontSize: 'var(--text-lg)' }}>
                  {result.quote.quoteNumber}
                </h2>
                <p className="text-sm text-muted" style={{ marginTop: 'var(--space-1)' }}>
                  {result.quote.customerName} · {result.quote.vehicleDescription}
                </p>
              </div>
              <StatusPill status={result.quote.status} />
            </div>

            <div className="card__body">
              <dl className="summary-list">
                <div className="summary-item">
                  <dt>Best annual premium</dt>
                  <dd>
                    {result.quote.annualPremium !== undefined
                      ? formatCurrency(result.quote.annualPremium)
                      : '—'}
                  </dd>
                </div>
                <div className="summary-item">
                  <dt>Options available</dt>
                  <dd>{result.quote.offerCount}</dd>
                </div>
              </dl>

              {result.quote.decisionReason && (
                <p className="text-sm" style={{ marginTop: 'var(--space-4)' }}>
                  {result.quote.decisionReason}
                </p>
              )}
            </div>

            {result.quote.status === 'Rated' && (
              <div className="card__footer">
                <Link
                  to={`/quotes/${result.quote.quoteId}`}
                  className="btn btn--accent btn--block"
                >
                  Open the full comparison
                </Link>
              </div>
            )}
          </section>

          {!result.quote.isTerminal && (
            <Alert variant="info" title="Still underwriting">
              <p>
                This quote is still being priced. Open the full comparison to watch it
                complete.
              </p>
            </Alert>
          )}

          {result.comparison && result.comparison.offers.length > 0 && (
            <section>
              <div className="section-heading">
                <h2 style={{ fontSize: 'var(--text-lg)' }}>
                  Your {result.comparison.offers.length} options
                </h2>
              </div>

              {/* Read-only: no onBind. Binding a policy is an authenticated agent
                  action, not something a two-factor lookup should permit. */}
              <div className="offer-grid">
                {result.comparison.offers.map((offer) => (
                  <OfferCard
                    key={offer.offerId}
                    offer={offer}
                    baselinePremium={result.comparison?.baselineAnnualPremium}
                    isSelected={selectedOfferId === offer.offerId}
                    onSelect={setSelectedOfferId}
                  />
                ))}
              </div>
            </section>
          )}
        </>
      )}

      <section className="card">
        <div className="card__body text-sm stack stack--sm">
          <h2 style={{ fontSize: 'var(--text-base)' }}>Don't have a quote yet?</h2>
          <p className="text-muted">
            Start one and compare all five carriers in a few seconds.
          </p>
          <Link to="/quotes/new" className="btn btn--secondary btn--sm btn--block">
            Start a new quote
          </Link>
        </div>
      </section>
    </div>
  );
}
