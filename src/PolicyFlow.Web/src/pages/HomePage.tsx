import { useEffect, useState } from 'react';
import { Link } from 'react-router-dom';
import { api } from '../api/client';
import type { CarrierSummary } from '../api/types';

/**
 * Landing page. Explains the marketplace and shows the live carrier panel read
 * from the NoSQL catalog, so the carrier list on screen is the same data the
 * rating engine uses rather than hard-coded markup.
 */
export function HomePage() {
  const [carriers, setCarriers] = useState<CarrierSummary[]>([]);
  const [carriersFailed, setCarriersFailed] = useState(false);

  useEffect(() => {
    const controller = new AbortController();

    api
      .getCarriers(controller.signal)
      .then(setCarriers)
      .catch((error: unknown) => {
        // Not fatal: the landing page is still useful without the panel, so it
        // degrades to hiding the section rather than showing an error.
        if (!(error instanceof DOMException)) {
          setCarriersFailed(true);
        }
      });

    return () => controller.abort();
  }, []);

  return (
    <div className="stack stack--lg">
      <section className="hero">
        <h1>Compare five carriers in one quote</h1>
        <p className="hero__lede">
          Enter your details once. PolicyFlow shops your risk against every carrier
          on the panel and returns up to fifteen priced coverage options, with the
          full rating calculation for each one.
        </p>
        <div className="hero__actions">
          <Link to="/quotes/new" className="btn btn--on-dark">
            Start a quote
          </Link>
          <Link to="/lookup" className="btn btn--ghost-on-dark">
            Find an existing quote
          </Link>
        </div>
      </section>

      {carriers.length > 0 && (
        <section>
          <div className="section-heading">
            <h2>The carrier panel</h2>
            <p className="text-sm text-muted">
              {carriers.length} carriers · 3 coverage tiers each
            </p>
          </div>

          <div className="carrier-strip">
            {carriers.map((carrier) => (
              <div
                key={carrier.carrierId}
                className="carrier-chip"
                style={{ '--carrier-color': carrier.brandColor } as React.CSSProperties}
              >
                <p className="carrier-chip__name">{carrier.displayName}</p>
                <p className="carrier-chip__meta">{carrier.tagline}</p>
                <p className="carrier-chip__meta">
                  Rated {carrier.financialStrengthRating} · Writes{' '}
                  {carrier.statesWritten.join(', ')}
                </p>
              </div>
            ))}
          </div>
        </section>
      )}

      {carriersFailed && (
        <section>
          <p className="text-sm text-muted">
            The carrier panel could not be loaded, but quoting still works — the
            rating engine falls back to its built-in panel.
          </p>
        </section>
      )}

      <section className="card">
        <div className="card__header">
          <h2>How this works</h2>
        </div>
        <div className="card__body stack">
          <div className="summary-list summary-list--wide">
            <div className="summary-item">
              <dt>1. Submit</dt>
              <dd style={{ fontSize: 'var(--text-sm)', fontWeight: 400 }}>
                The API accepts your quote and returns immediately. It does not wait
                on the carriers.
              </dd>
            </div>
            <div className="summary-item">
              <dt>2. Underwrite</dt>
              <dd style={{ fontSize: 'var(--text-sm)', fontWeight: 400 }}>
                A background worker verifies prior coverage, then requests pricing
                from all five carriers at once.
              </dd>
            </div>
            <div className="summary-item">
              <dt>3. Compare</dt>
              <dd style={{ fontSize: 'var(--text-sm)', fontWeight: 400 }}>
                Options arrive cheapest-first, each with the limits, discounts and
                the arithmetic behind the price.
              </dd>
            </div>
            <div className="summary-item">
              <dt>4. Bind and pay</dt>
              <dd style={{ fontSize: 'var(--text-sm)', fontWeight: 400 }}>
                Choose an option to create a policy, then make a premium payment.
              </dd>
            </div>
          </div>
        </div>
      </section>
    </div>
  );
}
