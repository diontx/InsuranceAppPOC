import { act } from 'react';
import { renderHook, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ApiError, api } from '../api/client';
import type { UnderwritingReview } from '../api/types';
import { describeOutcome, useUnderwritingWorkbench } from '../hooks/useUnderwritingWorkbench';
import { queue, queueItem, review } from './underwritingFixtures';

/** A promise the test resolves by hand, to control response ordering. */
function deferred<T>() {
  let resolve!: (value: T) => void;
  const promise = new Promise<T>((r) => {
    resolve = r;
  });
  return { promise, resolve };
}

const conflict = () =>
  new ApiError(
    409,
    {
      status: 409,
      title: 'Conflicting operation',
      detail: 'Quote Q-2026-000101 is Declined and is no longer awaiting underwriter review.',
    },
    'corr-409',
  );

describe('useUnderwritingWorkbench', () => {
  async function withOpenQuote() {
    vi.spyOn(api, 'getUnderwritingReview').mockResolvedValue(review());
    const hook = renderHook(() => useUnderwritingWorkbench());
    await act(async () => {
      await hook.result.current.select('quote-1');
    });
    return hook;
  }

  it('loads the queue', async () => {
    vi.spyOn(api, 'getUnderwritingQueue').mockResolvedValue(queue([queueItem(), queueItem({ quoteId: 'q2' })]));
    const { result } = renderHook(() => useUnderwritingWorkbench());

    await act(async () => {
      await result.current.loadQueue();
    });

    expect(result.current.queue?.items).toHaveLength(2);
    expect(result.current.isQueueLoading).toBe(false);
  });

  it('keeps a queue failure as state instead of throwing', async () => {
    vi.spyOn(api, 'getUnderwritingQueue').mockRejectedValue(new TypeError('Failed to fetch'));
    const { result } = renderHook(() => useUnderwritingWorkbench());

    await act(async () => {
      await result.current.loadQueue();
    });

    expect(result.current.queueError?.message).toBe('Failed to fetch');
  });

  it('shows the quote picked last, even when an earlier response arrives later', async () => {
    // Click A, then B; A's response is slow. Deciding on A while looking at B's
    // details is the bug this prevents.
    const slowA = deferred<UnderwritingReview>();
    const fastB = deferred<UnderwritingReview>();
    vi.spyOn(api, 'getUnderwritingReview').mockImplementation((quoteId) =>
      quoteId === 'a' ? slowA.promise : fastB.promise,
    );
    const { result } = renderHook(() => useUnderwritingWorkbench());

    let selectA!: Promise<void>;
    let selectB!: Promise<void>;
    act(() => {
      selectA = result.current.select('a');
      selectB = result.current.select('b');
    });

    await act(async () => {
      fastB.resolve(review({ quoteId: 'b', quoteNumber: 'Q-B' }));
      await selectB;
    });
    await act(async () => {
      slowA.resolve(review({ quoteId: 'a', quoteNumber: 'Q-A' }));
      await selectA;
    });

    expect(result.current.selectedQuoteId).toBe('b');
    expect(result.current.review?.quoteNumber).toBe('Q-B');
    expect(result.current.isReviewLoading).toBe(false);
  });

  it('after approval shows the rated quote, confirms it and refreshes the queue', async () => {
    const { result } = await withOpenQuote();
    const getQueue = vi.spyOn(api, 'getUnderwritingQueue').mockResolvedValue(queue([]));
    vi.spyOn(api, 'decideReferral').mockResolvedValue(
      review({ status: 'Rated', isAwaitingReview: false, offerCount: 9, annualPremium: 1234.5 }),
    );

    let saved = false;
    await act(async () => {
      saved = await result.current.decide('Approve', '   ');
    });

    expect(saved).toBe(true);
    expect(api.decideReferral).toHaveBeenCalledWith('quote-1', { decision: 'Approve', note: undefined });
    expect(result.current.review?.status).toBe('Rated');
    expect(result.current.lastOutcome?.message).toBe(
      'Q-2026-000101 approved and rated: 9 carrier offers, best annual premium $1,234.50.',
    );
    expect(getQueue).toHaveBeenCalledTimes(1);
  });

  it('trims the note before sending it', async () => {
    const { result } = await withOpenQuote();
    vi.spyOn(api, 'getUnderwritingQueue').mockResolvedValue(queue([]));
    const decide = vi.spyOn(api, 'decideReferral').mockResolvedValue(review({ status: 'FollowUp' }));

    await act(async () => {
      await result.current.decide('FollowUp', '  Upload proof of prior insurance.  ');
    });

    expect(decide).toHaveBeenCalledWith('quote-1', {
      decision: 'FollowUp',
      note: 'Upload proof of prior insurance.',
    });
  });

  it('on a conflict keeps the explanation and reloads both the queue and the quote', async () => {
    const { result } = await withOpenQuote();
    vi.spyOn(api, 'decideReferral').mockRejectedValue(conflict());
    const getQueue = vi.spyOn(api, 'getUnderwritingQueue').mockResolvedValue(queue([]));
    vi.spyOn(api, 'getUnderwritingReview').mockResolvedValue(
      review({ status: 'Declined', isAwaitingReview: false, reviewedBy: 'a.lindqvist' }),
    );

    await act(async () => {
      await result.current.decide('Approve', '');
    });

    expect((result.current.decisionError as ApiError).status).toBe(409);
    expect(result.current.review?.status).toBe('Declined');
    expect(getQueue).toHaveBeenCalledTimes(1);
  });

  it('on a business-rule rejection leaves the quote open and remembers the note', async () => {
    const { result } = await withOpenQuote();
    vi.spyOn(api, 'decideReferral').mockRejectedValue(
      new ApiError(422, { status: 422, title: 'Business rule violation', detail: 'No carrier will write it.' }),
    );
    const getQueue = vi.spyOn(api, 'getUnderwritingQueue');

    await act(async () => {
      await result.current.decide('Approve', ' Checked by phone. ');
    });

    expect(result.current.decisionError?.message).toBe('No carrier will write it.');
    expect(result.current.rejectedNote).toBe('Checked by phone.');
    expect(result.current.review?.isAwaitingReview).toBe(true);
    expect(getQueue).not.toHaveBeenCalled();
  });

  it('sends one decision even if the underwriter clicks twice in the same render', async () => {
    const { result } = await withOpenQuote();
    const response = deferred<UnderwritingReview>();
    const decide = vi.spyOn(api, 'decideReferral').mockReturnValue(response.promise);
    vi.spyOn(api, 'getUnderwritingQueue').mockResolvedValue(queue([]));

    let first!: Promise<boolean>;
    let second!: Promise<boolean>;
    act(() => {
      // Both calls see the same render's state; only the ref can stop the second.
      first = result.current.decide('Decline', 'Unable to verify any driving history.');
      second = result.current.decide('Decline', 'Unable to verify any driving history.');
    });

    expect(await second).toBe(false);
    expect(decide).toHaveBeenCalledTimes(1);
    await waitFor(() => expect(result.current.isSubmitting).toBe(true));

    await act(async () => {
      response.resolve(review({ status: 'Declined', isAwaitingReview: false }));
      expect(await first).toBe(true);
    });
  });

  it('clears the previous decision error when another quote is opened', async () => {
    const { result } = await withOpenQuote();
    vi.spyOn(api, 'decideReferral').mockRejectedValue(conflict());
    vi.spyOn(api, 'getUnderwritingQueue').mockResolvedValue(queue([]));
    await act(async () => {
      await result.current.decide('Approve', '');
    });

    await act(async () => {
      await result.current.select('quote-2');
    });

    expect(result.current.decisionError).toBeNull();
  });

  it('does nothing when no quote is open', async () => {
    const decide = vi.spyOn(api, 'decideReferral');
    const { result } = renderHook(() => useUnderwritingWorkbench());

    let saved = true;
    await act(async () => {
      saved = await result.current.decide('Approve', '');
    });

    expect(saved).toBe(false);
    expect(decide).not.toHaveBeenCalled();
  });
});

describe('describeOutcome', () => {
  it('reports an approval with the offer count and best premium', () => {
    expect(describeOutcome(review({ offerCount: 1, annualPremium: 980 }), 'Approve').message).toBe(
      'Q-2026-000101 approved and rated: 1 carrier offer, best annual premium $980.00.',
    );
  });

  it('quotes the follow-up request the applicant will see', () => {
    expect(
      describeOutcome(review({ decisionReason: 'Upload proof of prior insurance.' }), 'FollowUp').message,
    ).toContain('"Upload proof of prior insurance."');
  });

  it('reports a decline', () => {
    expect(describeOutcome(review(), 'Decline').message).toBe('Q-2026-000101 declined.');
  });
});
