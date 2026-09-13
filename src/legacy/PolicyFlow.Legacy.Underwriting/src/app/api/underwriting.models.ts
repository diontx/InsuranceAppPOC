/**
 * Wire types for the underwriting endpoints under `/api/underwriting`.
 *
 * Hand-written to mirror `PolicyFlow.Application/Underwriting/UnderwritingViews.cs`.
 * Optional members are the ones the API omits when null — it serialises with
 * `WhenWritingNull` — so the absent value is `undefined`, never `null`.
 */

/** The two statuses that put a quote in the work queue. */
export type QueueStatus = 'Referral' | 'FollowUp';

export type QuoteStatus = 'Pending' | 'Processing' | 'Rated' | 'Declined' | QueueStatus;

/** Matches the `ReviewDecision` enum names the API accepts. */
export type ReviewDecision = 'Approve' | 'FollowUp' | 'Decline';

export type QuoteChannel = 'Modern' | 'LegacyWebForms';

export interface UnderwritingQueueItem {
  quoteId: string;
  quoteNumber: string;
  customerName: string;
  state: string;
  vehicleDescription: string;
  status: QueueStatus;
  /** Why it was referred, or what the follow-up asked for. */
  reason?: string;
  channel: QuoteChannel;
  submittedUtc: string;
  updatedUtc: string;
  reviewedBy?: string;
}

export interface UnderwritingQueue {
  /** Oldest submission first, so the longest wait is at the top. */
  items: UnderwritingQueueItem[];
  referralCount: number;
  followUpCount: number;
  /** True when the server capped the list; the UI must not imply it is complete. */
  isTruncated: boolean;
  generatedUtc: string;
}

export interface CarrierAppetite {
  carrierId: string;
  carrierName: string;
  willWrite: boolean;
  /** Present only when the carrier will not write the risk. */
  reason?: string;
}

export interface UnderwritingReview {
  quoteId: string;
  quoteNumber: string;
  status: QuoteStatus;
  /** False once a decision has closed the referral. */
  isAwaitingReview: boolean;

  customerName: string;
  state: string;
  vehicleYear: number;
  vehicleMake: string;
  vehicleModel: string;
  annualMileage: number;
  priorInsuranceMonths: number;
  channel: QuoteChannel;
  correlationId: string;
  submittedUtc: string;
  updatedUtc: string;

  /** Referral reason, follow-up request or decline reason. */
  decisionReason?: string;

  /** Computed by the domain's QuoteRatingService — the UI knows no rules. */
  eligibilityOutcome: 'Eligible' | 'Refer' | 'Decline';
  baselineAnnualPremium?: number;
  eligibilityTrace: string[];
  carrierAppetite: CarrierAppetite[];

  reviewedBy?: string;
  reviewedUtc?: string;
  reviewNote?: string;
  annualPremium?: number;
  bestCarrierId?: string;
  offerCount: number;
}

export interface DecisionRequest {
  decision: ReviewDecision;
  note?: string;
}

/**
 * Note length rules, mirrored from `ReviewReferralCommandValidator` and
 * `Quote.MaxReviewNoteLength` so the form can say so before a round trip. The
 * server remains the authority: these only save the underwriter a 400.
 */
export const NOTE_MIN_LENGTH = 10;
export const NOTE_MAX_LENGTH = 500;
