/**
 * Wire types for the PolicyFlow API.
 *
 * Hand-written rather than generated, so the client contract is reviewable in one
 * place. They mirror the DTOs in `PolicyFlow.Api/Contracts` and the application
 * layer's view models.
 */

/**
 * Quote lifecycle. `Rated`, `Referral`, `Declined` and `FollowUp` are terminal.
 * `FollowUp` means an underwriter has asked the applicant for more information.
 */
export type QuoteStatus =
  | 'Pending'
  | 'Processing'
  | 'Rated'
  | 'Referral'
  | 'Declined'
  | 'FollowUp';

/** The three coverage packages every carrier files. */
export type CoverageTier = 'Basic' | 'Standard' | 'Premium';

export type PolicyStatus = 'Active' | 'Cancelled' | 'Expired';

/** Request body for `POST /api/quotes`. */
export interface SubmitQuoteRequest {
  customerName: string;
  state: string;
  vehicleYear: number;
  vehicleMake: string;
  vehicleModel: string;
  annualMileage: number;
  priorInsuranceMonths: number;
}

/** Response body for an accepted submission (HTTP 202). */
export interface SubmitQuoteResponse {
  quoteId: string;
  quoteNumber: string;
  status: QuoteStatus;
  statusUrl: string;
  correlationId: string;
}

/** The shape the status endpoint returns and the client polls. */
export interface QuoteStatusView {
  quoteId: string;
  quoteNumber: string;
  status: QuoteStatus;
  /** True once no further automated processing will change the status. */
  isTerminal: boolean;
  customerName: string;
  state: string;
  vehicleDescription: string;
  annualMileage: number;
  priorInsuranceMonths: number;
  /** Best available annual premium. Absent unless `Rated`. */
  annualPremium?: number;
  bestCarrierId?: string;
  bestCoverageTier?: CoverageTier;
  /** Explanation for a `Referral` or `Declined` outcome. */
  decisionReason?: string;
  offerCount: number;
  channel: 'Modern' | 'LegacyWebForms';
  correlationId: string;
  createdUtc: string;
  updatedUtc: string;
  offersUrl?: string;
}

/** One priced coverage option from one carrier. */
export interface ComparisonOffer {
  offerId: string;
  carrierId: string;
  carrierName: string;
  tagline: string;
  /** Carrier brand colour, applied as a CSS custom property on the card. */
  brandColor: string;
  financialStrengthRating: string;
  coverageTier: CoverageTier;
  annualPremium: number;
  monthlyPremium: number;
  deductible: number;
  /** Limits in the conventional shorthand, e.g. `50/100/50`. */
  limitsSummary: string;
  bodilyInjuryPerPerson: number;
  bodilyInjuryPerAccident: number;
  propertyDamage: number;
  includesRoadside: boolean;
  includesRentalReimbursement: boolean;
  discountAmount: number;
  appliedDiscounts: string[];
  /** Step-by-step rating explanation, shown in an expandable panel. */
  ratingTrace: string[];
  /** True for the cheapest offer in the whole panel. */
  isBestPrice: boolean;
}

/** A carrier that returned no offer, and why. */
export interface CarrierDeclination {
  carrierId: string;
  carrierName: string;
  reason: string;
  /** True when the gateway failed rather than the carrier declining. */
  wasUnavailable: boolean;
}

/** The denormalised comparison document. */
export interface QuoteComparison {
  quoteId: string;
  quoteNumber: string;
  customerName: string;
  state: string;
  vehicleDescription: string;
  status: QuoteStatus;
  /** House reference premium, shown beside the carrier panel for contrast. */
  baselineAnnualPremium?: number;
  offers: ComparisonOffer[];
  declinations: CarrierDeclination[];
  generatedUtc: string;
  correlationId: string;
  /** Wall-clock milliseconds for the whole underwriting run. */
  totalRatingMs: number;
}

/** Combined payload for the mobile lookup, so a phone needs one round trip. */
export interface QuoteLookupResponse {
  quote: QuoteStatusView;
  comparison?: QuoteComparison;
}

export interface PaymentView {
  paymentId: string;
  amount: number;
  status: 'Approved' | 'Declined';
  externalTransactionId: string;
  createdUtc: string;
  /**
   * True when the API replayed an existing payment instead of creating one.
   * The demo surfaces this to prove idempotency worked.
   */
  wasReplayed: boolean;
}

export interface PolicyView {
  policyId: string;
  policyNumber: string;
  quoteId: string;
  quoteNumber: string;
  status: PolicyStatus;
  insuredName: string;
  state: string;
  carrierId: string;
  carrierName: string;
  coverageTier: CoverageTier;
  annualPremium: number;
  monthlyPremium: number;
  deductible: number;
  totalPaid: number;
  balanceDue: number;
  effectiveDate: string;
  expirationDate: string;
  payments: PaymentView[];
  correlationId: string;
  createdUtc: string;
}

export interface CoverageTierSummary {
  tier: CoverageTier;
  deductible: number;
  limitsSummary: string;
  bodilyInjuryPerPerson: number;
  bodilyInjuryPerAccident: number;
  propertyDamage: number;
  includesRoadside: boolean;
  includesRentalReimbursement: boolean;
}

export interface CarrierSummary {
  carrierId: string;
  displayName: string;
  tagline: string;
  brandColor: string;
  financialStrengthRating: string;
  statesWritten: string[];
  minimumPriorInsuranceMonths: number;
  maximumAnnualMileage: number;
  coverageTiers: CoverageTierSummary[];
  availableDiscounts: string[];
}

/**
 * RFC 9457 problem detail. The API returns this shape for every failure, so the
 * client has exactly one error contract to handle.
 */
export interface ProblemDetails {
  type?: string;
  title?: string;
  status?: number;
  detail?: string;
  instance?: string;
  /** Per-field messages on a 400 from validation. */
  errors?: Record<string, string[]>;
  correlationId?: string;
  traceId?: string;
}
