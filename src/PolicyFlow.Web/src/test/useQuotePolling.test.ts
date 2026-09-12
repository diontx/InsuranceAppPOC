import { renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, api } from '../api/client';
import { useQuotePolling } from '../hooks/useQuotePolling';
import type { QuoteComparison, QuoteStatusView } from '../api/types';

/**
 * The behaviour that matters: polling must stop the moment underwriting reaches a
 * terminal status. A loop that kept going would hammer the API forever on every
 * open tab.
 */
describe('useQuotePolling', () => {
  const QUOTE_ID = 'c0ffee00-0000-4000-8000-000000000002';

  function quote(overrides: Partial<QuoteStatusView> = {}): QuoteStatusView {
    return {
      quoteId: QUOTE_ID,
      quoteNumber: 'Q-2026-000001',
      status: 'Pending',
      isTerminal: false,
      customerName: 'Alex Johnson',
      state: 'GA',
      vehicleDescription: '2024 Toyota Camry',
      annualMileage: 12000,
      priorInsuranceMonths: 36,
      offerCount: 0,
      channel: 'Modern',
      correlationId: 'corr-1',
      createdUtc: '2026-06-15T12:00:00Z',
      updatedUtc: '2026-06-15T12:00:00Z',
      ...overrides,
    };
  }

  function comparison(): QuoteComparison {
    return {
      quoteId: QUOTE_ID,
      quoteNumber: 'Q-2026-000001',
      customerName: 'Alex Johnson',
      state: 'GA',
      vehicleDescription: '2024 Toyota Camry',
      status: 'Rated',
      baselineAnnualPremium: 1075,
      offers: [],
      declinations: [],
      generatedUtc: '2026-06-15T12:00:02Z',
      correlationId: 'corr-1',
      totalRatingMs: 1240,
    };
  }

  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it('stops polling as soon as the quote is Rated', async () => {
    const getQuote = vi
      .spyOn(api, 'getQuote')
      .mockResolvedValue(quote({ status: 'Rated', isTerminal: true, offerCount: 15 }));
    vi.spyOn(api, 'getQuoteOffers').mockResolvedValue(comparison());

    const { result } = renderHook(() => useQuotePolling(QUOTE_ID));

    await waitFor(() => expect(result.current.quote?.status).toBe('Rated'));
    await waitFor(() => expect(result.current.comparison).not.toBeNull());

    expect(result.current.isPolling).toBe(false);

    // Wait past two poll intervals. A single read means the loop genuinely
    // stopped rather than being merely slow.
    await new Promise((resolve) => setTimeout(resolve, 3500));
    expect(getQuote).toHaveBeenCalledTimes(1);
  }, 10000);

  it.each([
    ['Referral' as const],
    ['Declined' as const],
  ])('stops polling on the terminal status %s', async (status) => {
    const getQuote = vi
      .spyOn(api, 'getQuote')
      .mockResolvedValue(quote({ status, isTerminal: true, decisionReason: 'test reason' }));
    const getOffers = vi.spyOn(api, 'getQuoteOffers');

    const { result } = renderHook(() => useQuotePolling(QUOTE_ID));

    await waitFor(() => expect(result.current.quote?.status).toBe(status));

    expect(result.current.isPolling).toBe(false);
    // No offers exist for a referral or decline, so the comparison must not be
    // requested at all.
    expect(getOffers).not.toHaveBeenCalled();

    await new Promise((resolve) => setTimeout(resolve, 3500));
    expect(getQuote).toHaveBeenCalledTimes(1);
  }, 10000);

  it('keeps polling while the quote is Pending, then settles when it rates', async () => {
    const getQuote = vi
      .spyOn(api, 'getQuote')
      .mockResolvedValueOnce(quote({ status: 'Pending' }))
      .mockResolvedValueOnce(quote({ status: 'Processing' }))
      .mockResolvedValue(quote({ status: 'Rated', isTerminal: true, offerCount: 12 }));
    vi.spyOn(api, 'getQuoteOffers').mockResolvedValue(comparison());

    const { result } = renderHook(() => useQuotePolling(QUOTE_ID));

    await waitFor(() => expect(result.current.quote?.status).toBe('Pending'));
    expect(result.current.isPolling).toBe(true);

    await waitFor(() => expect(result.current.quote?.status).toBe('Rated'), {
      timeout: 8000,
    });

    expect(result.current.isPolling).toBe(false);
    expect(getQuote).toHaveBeenCalledTimes(3);
  }, 15000);

  it('fetches the comparison only when the rated quote has offers', async () => {
    vi.spyOn(api, 'getQuote').mockResolvedValue(
      quote({ status: 'Rated', isTerminal: true, offerCount: 0 }),
    );
    const getOffers = vi.spyOn(api, 'getQuoteOffers');

    const { result } = renderHook(() => useQuotePolling(QUOTE_ID));

    await waitFor(() => expect(result.current.quote?.isTerminal).toBe(true));

    expect(getOffers).not.toHaveBeenCalled();
  });

  it('stops immediately on a non-retryable error', async () => {
    const getQuote = vi
      .spyOn(api, 'getQuote')
      .mockRejectedValue(new ApiError(404, { status: 404, title: 'Quote not found' }));

    const { result } = renderHook(() => useQuotePolling(QUOTE_ID));

    await waitFor(() => expect(result.current.error).toBeInstanceOf(ApiError));

    expect(result.current.isPolling).toBe(false);
    // A 404 will never become a 200, so retrying would be pure noise.
    await new Promise((resolve) => setTimeout(resolve, 3500));
    expect(getQuote).toHaveBeenCalledTimes(1);
  }, 10000);

  it('does not call the API without a quote id', async () => {
    const getQuote = vi.spyOn(api, 'getQuote');

    const { result } = renderHook(() => useQuotePolling(undefined));

    expect(getQuote).not.toHaveBeenCalled();
    expect(result.current.isLoading).toBe(false);
  });

  it('still exposes the quote when the comparison fetch fails', async () => {
    // The status is the important result. A failed read-model fetch must not
    // present to the user as a failed quote.
    vi.spyOn(api, 'getQuote').mockResolvedValue(
      quote({ status: 'Rated', isTerminal: true, offerCount: 15, annualPremium: 1250 }),
    );
    vi.spyOn(api, 'getQuoteOffers').mockRejectedValue(
      new ApiError(500, { status: 500, title: 'Internal error' }),
    );

    const { result } = renderHook(() => useQuotePolling(QUOTE_ID));

    await waitFor(() => expect(result.current.error).not.toBeNull());

    expect(result.current.quote?.status).toBe('Rated');
    expect(result.current.quote?.annualPremium).toBe(1250);
    expect(result.current.comparison).toBeNull();
  });
});
