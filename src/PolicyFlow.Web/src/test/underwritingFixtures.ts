import type { UnderwritingQueue, UnderwritingQueueItem, UnderwritingReview } from '../api/types';

/** An API-style timestamp (no trailing Z) the given number of hours ago. */
export function hoursAgo(hours: number): string {
  return new Date(Date.now() - hours * 3_600_000).toISOString().replace('Z', '');
}

export function queueItem(overrides: Partial<UnderwritingQueueItem> = {}): UnderwritingQueueItem {
  return {
    quoteId: 'quote-1',
    quoteNumber: 'Q-2026-000101',
    customerName: 'Riley Morgan',
    state: 'GA',
    vehicleDescription: '2023 Honda Civic',
    status: 'Referral',
    reason: 'No prior insurance on record. A licensed underwriter must review this application.',
    channel: 'Modern',
    submittedUtc: hoursAgo(3),
    updatedUtc: hoursAgo(3),
    ...overrides,
  };
}

export function queue(items: UnderwritingQueueItem[]): UnderwritingQueue {
  return {
    items,
    referralCount: items.filter((item) => item.status === 'Referral').length,
    followUpCount: items.filter((item) => item.status === 'FollowUp').length,
    isTruncated: false,
    generatedUtc: hoursAgo(0),
  };
}

export function review(overrides: Partial<UnderwritingReview> = {}): UnderwritingReview {
  return {
    quoteId: 'quote-1',
    quoteNumber: 'Q-2026-000101',
    status: 'Referral',
    isAwaitingReview: true,
    customerName: 'Riley Morgan',
    state: 'GA',
    vehicleYear: 2023,
    vehicleMake: 'Honda',
    vehicleModel: 'Civic',
    annualMileage: 12000,
    priorInsuranceMonths: 0,
    channel: 'Modern',
    correlationId: 'corr-quote-1',
    submittedUtc: '2026-09-13T09:00:00',
    updatedUtc: '2026-09-13T09:00:02',
    decisionReason: 'No prior insurance on record. A licensed underwriter must review this application.',
    eligibilityOutcome: 'Refer',
    eligibilityTrace: ['REFER: no verifiable prior insurance; manual underwriting required.'],
    carrierAppetite: [
      { carrierId: 'liberty-crest', carrierName: 'Liberty Crest Insurance', willWrite: true },
      {
        carrierId: 'summit-shield',
        carrierName: 'Summit Shield Mutual',
        willWrite: false,
        reason: 'Summit Shield Mutual requires 6 months of prior coverage; quote has 0.',
      },
      { carrierId: 'blueridge-assurance', carrierName: 'BlueRidge Assurance', willWrite: true },
      { carrierId: 'patriot-vale', carrierName: 'Patriot Vale Casualty', willWrite: true },
      {
        carrierId: 'ironclad-direct',
        carrierName: 'Ironclad Direct',
        willWrite: false,
        reason: 'Ironclad Direct requires 12 months of prior coverage; quote has 0.',
      },
    ],
    offerCount: 0,
    ...overrides,
  };
}
