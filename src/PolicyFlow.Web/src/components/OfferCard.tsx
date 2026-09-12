import { formatCurrency, formatNumber } from '../api/client';
import type { ComparisonOffer } from '../api/types';

interface OfferCardProps {
  offer: ComparisonOffer;
  /** House reference premium, used to show the saving against the baseline. */
  baselinePremium?: number;
  isSelected: boolean;
  onSelect: (offerId: string) => void;
  /** Absent on the read-only mobile lookup view. */
  onBind?: (offerId: string) => void;
  isBinding?: boolean;
}

/**
 * One carrier's priced coverage option.
 *
 * Deliberately discloses the full rating trace. A comparison screen that shows
 * only a number invites "why is that the price?", and for a POC whose whole point
 * is explainable architecture, being able to expand any card and read the
 * arithmetic is the feature.
 */
export function OfferCard({
  offer,
  baselinePremium,
  isSelected,
  onSelect,
  onBind,
  isBinding = false,
}: OfferCardProps) {
  const className = [
    'offer-card',
    offer.isBestPrice ? 'offer-card--best' : '',
    isSelected ? 'offer-card--selected' : '',
  ]
    .filter(Boolean)
    .join(' ');

  const savingVsBaseline =
    baselinePremium !== undefined && baselinePremium > offer.annualPremium
      ? baselinePremium - offer.annualPremium
      : undefined;

  return (
    <article
      className={className}
      // The carrier's brand colour reaches CSS as a custom property, so each
      // carrier keeps its identity without a stylesheet rule per carrier.
      style={{ '--carrier-color': offer.brandColor } as React.CSSProperties}
      aria-label={`${offer.carrierName}, ${offer.coverageTier} coverage, ${formatCurrency(
        offer.annualPremium,
      )} per year`}
    >
      <div className="offer-card__brand-bar" aria-hidden="true" />

      {offer.isBestPrice && (
        <p className="offer-card__best-badge" style={{ marginTop: 'var(--space-3)' }}>
          Lowest price
        </p>
      )}

      <header className="offer-card__header">
        <div>
          <h3 className="offer-card__carrier">{offer.carrierName}</h3>
          <p className="offer-card__tagline">{offer.tagline}</p>
        </div>
        <span
          className="offer-card__rating"
          title="Financial strength rating (fictional)"
        >
          {offer.financialStrengthRating}
        </span>
      </header>

      <p className="offer-card__tier">{offer.coverageTier}</p>

      <div className="offer-card__price">
        <p className="offer-card__annual">
          {formatCurrency(offer.annualPremium)}
          <span className="offer-card__annual-unit">/ year</span>
        </p>
        <p className="offer-card__monthly">
          or {formatCurrency(offer.monthlyPremium)} / month
        </p>
        {savingVsBaseline !== undefined && (
          <p className="offer-card__savings">
            {formatCurrency(savingVsBaseline)} below the reference premium
          </p>
        )}
        {offer.discountAmount > 0 && (
          <p className="offer-card__savings">
            {formatCurrency(offer.discountAmount)} in discounts applied
          </p>
        )}
      </div>

      <dl className="offer-card__specs">
        <div className="offer-card__spec">
          <dt>Deductible</dt>
          <dd>{formatCurrency(offer.deductible)}</dd>
        </div>
        <div className="offer-card__spec">
          <dt>Bodily injury</dt>
          <dd>
            {formatNumber(offer.bodilyInjuryPerPerson / 1000)}k /{' '}
            {formatNumber(offer.bodilyInjuryPerAccident / 1000)}k
          </dd>
        </div>
        <div className="offer-card__spec">
          <dt>Property damage</dt>
          <dd>{formatNumber(offer.propertyDamage / 1000)}k</dd>
        </div>
      </dl>

      <div className="offer-card__features">
        <span
          className={`feature-tag ${offer.includesRoadside ? '' : 'feature-tag--absent'}`}
        >
          {offer.includesRoadside ? '✓' : '✕'} Roadside
        </span>
        <span
          className={`feature-tag ${
            offer.includesRentalReimbursement ? '' : 'feature-tag--absent'
          }`}
        >
          {offer.includesRentalReimbursement ? '✓' : '✕'} Rental
        </span>
      </div>

      {offer.appliedDiscounts.length > 0 && (
        <div className="offer-card__features">
          {offer.appliedDiscounts.map((discount) => (
            <span key={discount} className="feature-tag">
              {discount}
            </span>
          ))}
        </div>
      )}

      {/* <details> rather than React state: keyboard accessible and functional
          without any JS of ours. */}
      <details className="rating-trace">
        <summary className="rating-trace__summary">
          How this price was calculated
        </summary>
        <ul className="rating-trace__body">
          {offer.ratingTrace.map((line, index) => (
            <li key={index}>{line}</li>
          ))}
        </ul>
      </details>

      <div className="offer-card__actions">
        {onBind ? (
          <>
            <button
              type="button"
              className={`btn btn--block ${isSelected ? 'btn--accent' : 'btn--primary'}`}
              onClick={() => onBind(offer.offerId)}
              disabled={isBinding}
            >
              {isBinding ? (
                <>
                  <span className="spinner" aria-hidden="true" />
                  Binding…
                </>
              ) : (
                'Buy this policy'
              )}
            </button>
            <button
              type="button"
              className="btn btn--secondary btn--sm btn--block"
              onClick={() => onSelect(offer.offerId)}
              aria-pressed={isSelected}
            >
              {isSelected ? 'Selected for comparison' : 'Compare'}
            </button>
          </>
        ) : (
          <button
            type="button"
            className="btn btn--secondary btn--block"
            onClick={() => onSelect(offer.offerId)}
            aria-pressed={isSelected}
          >
            {isSelected ? 'Selected' : 'Select'}
          </button>
        )}
      </div>
    </article>
  );
}
