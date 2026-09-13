import { HttpErrorResponse } from '@angular/common/http';
import { TestBed } from '@angular/core/testing';
import { Observable, Subject, of, throwError } from 'rxjs';

import { UnderwritingApiService } from '../api/underwriting-api.service';
import {
  DecisionRequest,
  UnderwritingQueue,
  UnderwritingReview,
} from '../api/underwriting.models';
import { queue, queueItem, review } from '../testing/fixtures';
import { UnderwritingStore, describeOutcome } from './underwriting-store';

describe('UnderwritingStore', () => {
  let api: {
    getQueue: ReturnType<typeof vi.fn<() => Observable<UnderwritingQueue>>>;
    getReview: ReturnType<typeof vi.fn<(quoteId: string) => Observable<UnderwritingReview>>>;
    decide: ReturnType<typeof vi.fn<(quoteId: string, request: DecisionRequest) => Observable<UnderwritingReview>>>;
  };
  let store: UnderwritingStore;

  beforeEach(() => {
    api = {
      getQueue: vi.fn(() => of(queue([]))),
      getReview: vi.fn((quoteId: string) => of(review({ quoteId }))),
      decide: vi.fn(),
    };

    TestBed.configureTestingModule({
      providers: [{ provide: UnderwritingApiService, useValue: api }],
    });

    store = TestBed.inject(UnderwritingStore);
  });

  const conflict = () =>
    new HttpErrorResponse({
      status: 409,
      statusText: 'Conflict',
      error: {
        title: 'Conflicting operation',
        detail: 'Quote Q-2026-000101 is Declined and is no longer awaiting underwriter review.',
        correlationId: 'corr-409',
      },
    });

  // =====================================================================
  // Queue
  // =====================================================================

  it('loads the queue and exposes its items', async () => {
    api.getQueue.mockReturnValue(of(queue([queueItem(), queueItem({ quoteId: 'quote-2' })])));

    await store.loadQueue();

    expect(store.items()).toHaveLength(2);
    expect(store.queueLoading()).toBe(false);
    expect(store.queueError()).toBeNull();
  });

  it('records a failure to load the queue as a problem instead of throwing', async () => {
    api.getQueue.mockReturnValue(throwError(() => new HttpErrorResponse({ status: 0 })));

    await store.loadQueue();

    expect(store.queueError()?.title).toBe('Cannot reach the PolicyFlow API');
    expect(store.items()).toEqual([]);
    expect(store.queueLoading()).toBe(false);
  });

  // =====================================================================
  // Selection
  // =====================================================================

  it('opens the quote the underwriter selected', async () => {
    await store.select('quote-7');

    expect(api.getReview).toHaveBeenCalledWith('quote-7');
    expect(store.selectedQuoteId()).toBe('quote-7');
    expect(store.review()?.quoteId).toBe('quote-7');
    expect(store.reviewLoading()).toBe(false);
  });

  it('shows the quote picked last, even when an earlier response arrives later', async () => {
    // Click A, then B. A's response is slow. The underwriter must end up on B —
    // deciding on A while looking at B's details is the bug this prevents.
    const slowA = new Subject<UnderwritingReview>();
    const fastB = new Subject<UnderwritingReview>();
    api.getReview.mockImplementation((quoteId) => (quoteId === 'a' ? slowA : fastB));

    const selectA = store.select('a');
    const selectB = store.select('b');

    fastB.next(review({ quoteId: 'b', quoteNumber: 'Q-B' }));
    await selectB;
    slowA.next(review({ quoteId: 'a', quoteNumber: 'Q-A' }));
    await selectA;

    expect(store.selectedQuoteId()).toBe('b');
    expect(store.review()?.quoteNumber).toBe('Q-B');
    expect(store.reviewLoading()).toBe(false);
  });

  it('clears the previous decision error when another quote is opened', async () => {
    await store.select('quote-1');
    api.decide.mockReturnValue(
      throwError(() => new HttpErrorResponse({ status: 422, error: { title: 'Business rule violation' } })),
    );
    await store.decide('Approve', '');
    expect(store.decisionError()).not.toBeNull();

    await store.select('quote-2');

    expect(store.decisionError()).toBeNull();
  });

  // =====================================================================
  // Decisions
  // =====================================================================

  it('after approval shows the rated quote, confirms it in plain language and refreshes the queue', async () => {
    await store.select('quote-1');
    api.decide.mockReturnValue(
      of(review({ status: 'Rated', isAwaitingReview: false, offerCount: 9, annualPremium: 1234.5 })),
    );
    api.getQueue.mockClear();

    const saved = await store.decide('Approve', '');

    expect(saved).toBe(true);
    expect(store.review()?.status).toBe('Rated');
    expect(store.lastOutcome()?.message).toBe(
      'Q-2026-000101 approved and rated: 9 carrier offers, best annual premium $1,234.50.',
    );
    expect(api.getQueue).toHaveBeenCalledTimes(1);
    expect(store.submitting()).toBe(false);
  });

  it('trims the note, and leaves a blank note out of the request entirely', async () => {
    await store.select('quote-1');
    api.decide.mockReturnValue(of(review({ status: 'FollowUp' })));

    await store.decide('FollowUp', '   Upload proof of prior insurance.  ');
    await store.decide('Approve', '    ');

    expect(api.decide).toHaveBeenNthCalledWith(1, 'quote-1', {
      decision: 'FollowUp',
      note: 'Upload proof of prior insurance.',
    });
    expect(api.decide).toHaveBeenNthCalledWith(2, 'quote-1', { decision: 'Approve', note: undefined });
  });

  it('on a conflict keeps the explanation and reloads both the queue and the quote', async () => {
    await store.select('quote-1');
    api.decide.mockReturnValue(throwError(conflict));
    api.getReview.mockReturnValue(of(review({ status: 'Declined', isAwaitingReview: false })));
    api.getQueue.mockClear();

    const saved = await store.decide('Approve', '');

    expect(saved).toBe(false);
    expect(store.decisionError()?.status).toBe(409);
    expect(store.decisionError()?.correlationId).toBe('corr-409');
    expect(store.review()?.status).toBe('Declined');
    expect(api.getQueue).toHaveBeenCalledTimes(1);
  });

  it('on a business-rule rejection leaves the quote open for another decision', async () => {
    await store.select('quote-1');
    api.decide.mockReturnValue(
      throwError(
        () =>
          new HttpErrorResponse({
            status: 422,
            error: { title: 'Business rule violation', detail: 'No carrier on the panel will write quote Q-2026-000101.' },
          }),
      ),
    );
    api.getQueue.mockClear();
    api.getReview.mockClear();

    await store.decide('Approve', '');

    expect(store.decisionError()?.detail).toContain('No carrier');
    expect(store.review()?.isAwaitingReview).toBe(true);
    expect(api.getQueue).not.toHaveBeenCalled();
    expect(api.getReview).not.toHaveBeenCalled();
  });

  it('sends one decision even if the underwriter clicks twice', async () => {
    await store.select('quote-1');
    const response = new Subject<UnderwritingReview>();
    api.decide.mockReturnValue(response);

    const first = store.decide('Decline', 'Unable to verify any driving history.');
    const second = await store.decide('Decline', 'Unable to verify any driving history.');

    expect(second).toBe(false);
    expect(api.decide).toHaveBeenCalledTimes(1);

    response.next(review({ status: 'Declined', isAwaitingReview: false }));
    expect(await first).toBe(true);
  });

  it('does nothing when no quote is open', async () => {
    expect(await store.decide('Approve', '')).toBe(false);
    expect(api.decide).not.toHaveBeenCalled();
  });
});

describe('describeOutcome', () => {
  it('reports an approval with the offer count and best premium', () => {
    const outcome = describeOutcome(review({ offerCount: 1, annualPremium: 980 }), 'Approve');

    expect(outcome.message).toBe('Q-2026-000101 approved and rated: 1 carrier offer, best annual premium $980.00.');
  });

  it('quotes the follow-up request the applicant will see', () => {
    const outcome = describeOutcome(review({ decisionReason: 'Upload proof of prior insurance.' }), 'FollowUp');

    expect(outcome.message).toContain('"Upload proof of prior insurance."');
  });

  it('reports a decline', () => {
    expect(describeOutcome(review(), 'Decline').message).toBe('Q-2026-000101 declined.');
  });
});
