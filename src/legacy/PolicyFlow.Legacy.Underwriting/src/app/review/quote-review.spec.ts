import { ComponentFixture, TestBed } from '@angular/core/testing';

import { ApiProblem } from '../api/api-problem';
import { UnderwritingReview } from '../api/underwriting.models';
import { review } from '../testing/fixtures';
import { DecisionSubmission, QuoteReview } from './quote-review';

describe('QuoteReview', () => {
  beforeEach(() => TestBed.configureTestingModule({ imports: [QuoteReview] }));

  async function render(
    overrides: Partial<UnderwritingReview> = {},
    options: { error?: ApiProblem | null; submitting?: boolean } = {},
  ) {
    const fixture = TestBed.createComponent(QuoteReview);
    fixture.componentRef.setInput('review', review(overrides));
    fixture.componentRef.setInput('error', options.error ?? null);
    fixture.componentRef.setInput('submitting', options.submitting ?? false);

    const decided = vi.fn<(submission: DecisionSubmission) => void>();
    fixture.componentInstance.decided.subscribe(decided);

    await fixture.whenStable();
    return { fixture, decided, el: fixture.nativeElement as HTMLElement };
  }

  const text = (el: Element) => (el.textContent ?? '').replace(/\s+/g, ' ').trim();

  async function choose(fixture: ComponentFixture<QuoteReview>, decision: string) {
    (fixture.nativeElement as HTMLElement).querySelector<HTMLInputElement>(`#uw-decision-${decision}`)!.click();
    await fixture.whenStable();
  }

  async function typeNote(fixture: ComponentFixture<QuoteReview>, value: string) {
    const note = (fixture.nativeElement as HTMLElement).querySelector<HTMLTextAreaElement>('#uw-note')!;
    note.value = value;
    note.dispatchEvent(new Event('input'));
    await fixture.whenStable();
  }

  async function submit(fixture: ComponentFixture<QuoteReview>) {
    (fixture.nativeElement as HTMLElement)
      .querySelector('form')!
      .dispatchEvent(new Event('submit', { cancelable: true }));
    await fixture.whenStable();
  }

  // =====================================================================
  // The application
  // =====================================================================

  it('shows the application as read-only form fields', async () => {
    const { el } = await render();

    const applicant = el.querySelector<HTMLInputElement>('#uw-applicant')!;
    expect(applicant.value).toBe('Riley Morgan');
    expect(applicant.readOnly).toBe(true);
    expect(el.querySelector<HTMLInputElement>('#uw-vehicle-model')!.value).toBe('Civic');
    expect(el.querySelector<HTMLInputElement>('#uw-mileage')!.value).toBe('12,000');
    expect(el.querySelector<HTMLInputElement>('#uw-prior')!.value).toBe('0');
    expect(el.querySelector<HTMLInputElement>('#uw-baseline')!.value).toBe('Not priced');
  });

  it('explains why the quote was referred and which carriers would write it', async () => {
    const { el } = await render();

    expect(text(el)).toContain('Referred: No prior insurance on record.');
    expect(text(el)).toContain('REFER: no verifiable prior insurance; manual underwriting required.');
    expect(text(el)).toContain('3 of 5 carriers would price this risk if it is approved.');
    expect(text(el)).toContain('Ironclad Direct requires 12 months of prior coverage; quote has 0.');
  });

  // =====================================================================
  // Validation
  // =====================================================================

  it('will not send anything until a decision is chosen', async () => {
    const { fixture, decided, el } = await render();

    await submit(fixture);

    expect(decided).not.toHaveBeenCalled();
    expect(text(el)).toContain('Choose a decision.');
  });

  it('requires a note before a follow-up request can be sent', async () => {
    const { fixture, decided, el } = await render();

    await choose(fixture, 'FollowUp');
    await submit(fixture);

    expect(decided).not.toHaveBeenCalled();
    expect(text(el.querySelector('#uw-note-error')!)).toBe('Say what information is needed from the applicant.');
    expect(el.querySelector('#uw-note')!.getAttribute('aria-invalid')).toBe('true');
  });

  it('rejects a decline reason too short to act on', async () => {
    const { fixture, decided, el } = await render();

    await choose(fixture, 'Decline');
    await typeNote(fixture, '  No.  ');
    await submit(fixture);

    expect(decided).not.toHaveBeenCalled();
    expect(text(el.querySelector('#uw-note-error')!)).toBe('Note must be at least 10 characters.');
  });

  it('emits a decline with its reason, trimmed', async () => {
    const { fixture, decided } = await render();

    await choose(fixture, 'Decline');
    await typeNote(fixture, '  Unable to verify any driving history.  ');
    await submit(fixture);

    expect(decided).toHaveBeenCalledWith({
      decision: 'Decline',
      note: 'Unable to verify any driving history.',
    });
  });

  it('allows approval without a note', async () => {
    const { fixture, decided } = await render();

    await choose(fixture, 'Approve');
    await submit(fixture);

    expect(decided).toHaveBeenCalledWith({ decision: 'Approve', note: '' });
  });

  it('only offers approval when a carrier would write the risk, and says why not', async () => {
    const { el } = await render({
      decisionReason: 'No carrier on the panel will write this risk automatically.',
      carrierAppetite: [
        { carrierId: 'liberty-crest', carrierName: 'Liberty Crest Insurance', willWrite: false, reason: 'Mileage.' },
        { carrierId: 'patriot-vale', carrierName: 'Patriot Vale Casualty', willWrite: false, reason: 'Mileage.' },
      ],
    });

    expect(el.querySelector<HTMLInputElement>('#uw-decision-Approve')!.disabled).toBe(true);
    expect(el.querySelector<HTMLInputElement>('#uw-decision-FollowUp')!.disabled).toBe(false);
    expect(text(el)).toContain('Unavailable: no carrier on the panel will write this risk.');
    expect(text(el)).toContain('0 of 2 carriers');
  });

  // =====================================================================
  // Server responses
  // =====================================================================

  it('puts a server validation message beside the note', async () => {
    const { el } = await render(
      {},
      {
        error: {
          status: 400,
          title: 'One or more validation errors occurred',
          fieldErrors: { note: 'State the reason for declining.' },
        },
      },
    );

    expect(text(el.querySelector('#uw-note-error')!)).toBe('State the reason for declining.');
    // Field errors are not repeated as a general alert.
    expect(el.querySelector('.uw-alert--error')).toBeNull();
  });

  it('shows a conflict as a general error with the support reference, even once the form is gone', async () => {
    const { el } = await render(
      { status: 'Declined', isAwaitingReview: false, reviewedBy: 'a.lindqvist' },
      {
        error: {
          status: 409,
          title: 'Conflicting operation',
          detail: 'Quote Q-2026-000101 is Declined and is no longer awaiting underwriter review.',
          fieldErrors: {},
          correlationId: 'corr-409',
        },
      },
    );

    const alert = text(el.querySelector('.uw-alert--error')!);
    expect(alert).toContain('Conflicting operation.');
    expect(alert).toContain('no longer awaiting underwriter review');
    expect(alert).toContain('Reference: corr-409');
  });

  it('replaces the decision form with the outcome once the quote is decided', async () => {
    const { el } = await render({
      status: 'Rated',
      isAwaitingReview: false,
      decisionReason: undefined,
      reviewedBy: 'j.okafor',
      reviewNote: 'Lapse verified as a relocation.',
      offerCount: 9,
      annualPremium: 1234.5,
    });

    expect(el.querySelector('#uw-note')).toBeNull();
    expect(text(el)).toContain('Approved by j.okafor. 9 carrier offers are available to the applicant; best annual premium $1,234.50.');
    expect(text(el)).toContain('Lapse verified as a relocation.');
  });

  it('starts clean when a different quote is opened', async () => {
    const { fixture, el } = await render();
    await choose(fixture, 'Decline');
    await typeNote(fixture, 'Unable to verify any driving history.');

    fixture.componentRef.setInput('review', review({ quoteId: 'quote-2', quoteNumber: 'Q-2026-000102' }));
    await fixture.whenStable();

    expect(el.querySelector<HTMLInputElement>('#uw-decision-Decline')!.checked).toBe(false);
    expect(el.querySelector<HTMLTextAreaElement>('#uw-note')!.value).toBe('');
    expect(el.querySelector('#uw-note-error')).toBeNull();
  });

  it('cannot be submitted again while a decision is being saved', async () => {
    const { fixture, decided, el } = await render({}, { submitting: true });

    await choose(fixture, 'Approve');
    await submit(fixture);

    const button = el.querySelector<HTMLButtonElement>('button[type="submit"]')!;
    expect(button.disabled).toBe(true);
    expect(text(button)).toBe('Saving…');
    expect(decided).not.toHaveBeenCalled();
  });
});
