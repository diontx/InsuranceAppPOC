import { provideHttpClient, withInterceptors } from '@angular/common/http';
import { HttpTestingController, provideHttpClientTesting } from '@angular/common/http/testing';
import { ComponentFixture, TestBed } from '@angular/core/testing';

import { API_BASE_URL } from './api/api-config';
import { correlationIdInterceptor } from './api/correlation-id.interceptor';
import { App } from './app';
import { queue, queueItem, review } from './testing/fixtures';

/** Lets pending promise continuations (and the HTTP calls they make) run. */
const settle = () => new Promise<void>((resolve) => setTimeout(resolve));

/**
 * The store applies a response in a promise continuation, after the flush that
 * resolved it. Wait for that continuation, then for the render it scheduled —
 * `whenStable()` alone can resolve before the signal has even changed.
 */
async function render(fixture: ComponentFixture<App>) {
  await settle();
  await fixture.whenStable();
}

/**
 * The workbench end to end in the browser: real components, real store, real
 * HttpClient and interceptor. Only the network is faked.
 */
describe('App (underwriting workbench)', () => {
  let http: HttpTestingController;

  beforeEach(() => {
    TestBed.configureTestingModule({
      imports: [App],
      providers: [
        provideHttpClient(withInterceptors([correlationIdInterceptor])),
        provideHttpClientTesting(),
        { provide: API_BASE_URL, useValue: '' },
      ],
    });
    http = TestBed.inject(HttpTestingController);
  });

  afterEach(() => http.verify());

  const text = (el: Element | null) => (el?.textContent ?? '').replace(/\s+/g, ' ').trim();

  async function startWithOneReferral(fixture: ComponentFixture<App>) {
    await settle();
    http.expectOne('/api/underwriting/queue').flush(queue([queueItem()]));
    await render(fixture);
  }

  async function openQuote(fixture: ComponentFixture<App>) {
    const el = fixture.nativeElement as HTMLElement;
    el.querySelector<HTMLButtonElement>('[data-quote-number="Q-2026-000101"]')!.click();
    await settle();
    http.expectOne('/api/underwriting/quotes/quote-1').flush(review());
    await render(fixture);
  }

  it('works a referral from the queue through to an approved, rated quote', async () => {
    const fixture = TestBed.createComponent(App);
    const el = fixture.nativeElement as HTMLElement;

    await startWithOneReferral(fixture);
    expect(text(el)).toContain('Select a quote from the work queue');
    expect(text(el.querySelector('.uw-stats dt'))).toBe('Referrals');
    expect(text(el.querySelector('.uw-stats dd'))).toBe('1');

    await openQuote(fixture);
    expect(el.querySelector<HTMLInputElement>('#uw-applicant')!.value).toBe('Riley Morgan');

    el.querySelector<HTMLInputElement>('#uw-decision-Approve')!.click();
    await fixture.whenStable();
    el.querySelector('form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    await settle();

    const decision = http.expectOne({ method: 'POST', url: '/api/underwriting/quotes/quote-1/decision' });
    expect(decision.request.body).toEqual({ decision: 'Approve', note: undefined });
    expect(decision.request.headers.has('X-Correlation-Id')).toBe(true);
    decision.flush(
      review({
        status: 'Rated',
        isAwaitingReview: false,
        decisionReason: undefined,
        reviewedBy: 'demo-underwriter (unauthenticated)',
        offerCount: 9,
        annualPremium: 1234.5,
      }),
    );

    // Saving a decision refreshes the queue.
    await settle();
    http.expectOne('/api/underwriting/queue').flush(queue([]));
    await render(fixture);

    expect(text(el.querySelector('.uw-live'))).toContain(
      'Q-2026-000101 approved and rated: 9 carrier offers, best annual premium $1,234.50.',
    );
    expect(text(el)).toContain('No quotes are awaiting review.');
    expect(text(el)).toContain('Approved by demo-underwriter (unauthenticated).');
  });

  it('tells the underwriter why a decision failed when someone else decided first', async () => {
    const fixture = TestBed.createComponent(App);
    const el = fixture.nativeElement as HTMLElement;

    await startWithOneReferral(fixture);
    await openQuote(fixture);

    el.querySelector<HTMLInputElement>('#uw-decision-FollowUp')!.click();
    const note = el.querySelector<HTMLTextAreaElement>('#uw-note')!;
    note.value = 'Upload proof of prior insurance.';
    note.dispatchEvent(new Event('input'));
    await fixture.whenStable();
    el.querySelector('form')!.dispatchEvent(new Event('submit', { cancelable: true }));
    await settle();

    http.expectOne({ method: 'POST', url: '/api/underwriting/quotes/quote-1/decision' }).flush(
      {
        title: 'Conflicting operation',
        detail: 'Quote Q-2026-000101 is Declined and is no longer awaiting underwriter review. Refresh the queue.',
        correlationId: 'corr-409',
      },
      { status: 409, statusText: 'Conflict' },
    );
    await settle();

    // The store reloads both, so the screen shows what actually happened.
    http.expectOne('/api/underwriting/queue').flush(queue([]));
    http
      .expectOne('/api/underwriting/quotes/quote-1')
      .flush(review({ status: 'Declined', isAwaitingReview: false, reviewedBy: 'a.lindqvist' }));
    await render(fixture);

    expect(text(el.querySelector('.uw-alert--error'))).toContain('no longer awaiting underwriter review');
    expect(text(el.querySelector('.uw-alert--error'))).toContain('corr-409');
    expect(text(el)).toContain('Declined by a.lindqvist.');
  });

  it('says plainly when the API cannot be reached', async () => {
    const fixture = TestBed.createComponent(App);
    const el = fixture.nativeElement as HTMLElement;

    await settle();
    http
      .expectOne('/api/underwriting/queue')
      .error(new ProgressEvent('error'), { status: 0, statusText: 'Unknown Error' });
    await render(fixture);

    expect(text(el.querySelector('[role="alert"]'))).toContain('Cannot reach the PolicyFlow API');
  });
});
