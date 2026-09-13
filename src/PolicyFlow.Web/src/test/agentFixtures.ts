import type { QuoteStatusView } from '../api/types';

export function quoteView(overrides: Partial<QuoteStatusView> = {}): QuoteStatusView {
  return {
    quoteId: 'c0ffee00-0000-4000-8000-000000000101',
    quoteNumber: 'Q-2026-000101',
    status: 'Rated',
    isTerminal: true,
    customerName: 'Dana Reyes',
    state: 'GA',
    vehicleDescription: '2023 Honda Accord',
    annualMileage: 9000,
    priorInsuranceMonths: 24,
    annualPremium: 812.5,
    bestCarrierId: 'ironclad-direct',
    bestCoverageTier: 'Basic',
    offerCount: 15,
    channel: 'Modern',
    correlationId: 'corr-101',
    createdUtc: '2026-09-13T09:00:00',
    updatedUtc: '2026-09-13T09:00:04',
    ...overrides,
  };
}
