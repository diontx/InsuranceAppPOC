import { MemoryRouter } from 'react-router-dom';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { describe, expect, it, vi } from 'vitest';
import { ApiError, api } from '../api/client';
import type { UnderwritingQueueItem } from '../api/types';
import { UnderwritingPage } from '../pages/UnderwritingPage';
import { hoursAgo, queue, queueItem, review } from './underwritingFixtures';

/**
 * The work queue end to end in the browser: page, hook and components together.
 * Only the API client is replaced.
 */
describe('UnderwritingPage', () => {
  const items: UnderwritingQueueItem[] = [
    queueItem({ quoteId: 'quote-1', quoteNumber: 'Q-2026-000101', customerName: 'Riley Morgan' }),
    queueItem({
      quoteId: 'quote-2',
      quoteNumber: 'Q-2026-000102',
      customerName: 'Casey Alvarez',
      status: 'FollowUp',
      reason: 'Upload proof of prior insurance.',
      submittedUtc: hoursAgo(28),
    }),
    queueItem({ quoteId: 'quote-3', quoteNumber: 'Q-2026-000103', customerName: 'Jordan Blake', state: 'TX' }),
  ];

  function renderPage() {
    return render(
      <MemoryRouter>
        <UnderwritingPage />
      </MemoryRouter>,
    );
  }

  const queueRows = () => Array.from(document.querySelectorAll<HTMLButtonElement>('.queue-item'));

  async function openFirstQuote(user: ReturnType<typeof userEvent.setup>) {
    await user.click(await screen.findByRole('button', { name: /Q-2026-000101/ }));
    await screen.findByLabelText(/insured name/i);
  }

  // =====================================================================
  // Queue
  // =====================================================================

  it('lists each quote with its number, applicant, status and time in queue', async () => {
    vi.spyOn(api, 'getUnderwritingQueue').mockResolvedValue(queue(items));
    renderPage();

    const first = await screen.findByRole('button', { name: /Q-2026-000101/ });
    expect(first).toHaveTextContent('Riley Morgan');
    expect(first).toHaveTextContent('Referral');
    expect(first).toHaveTextContent('GA · 2023 Honda Civic');
    expect(first).toHaveTextContent('In queue 3h');

    expect(screen.getByRole('button', { name: /Q-2026-000102/ })).toHaveTextContent('Follow-up');
    expect(screen.getByRole('button', { name: /Q-2026-000102/ })).toHaveTextContent('In queue 1d 4h');

    // The header stats, not the "Referrals" filter button.
    const stats = within(document.querySelector<HTMLElement>('.workbench-stats')!);
    expect(stats.getByText('Referrals').nextElementSibling).toHaveTextContent('2');
    expect(stats.getByText('Follow-up').nextElementSibling).toHaveTextContent('1');
  });

  it('filters by status and searches by applicant or quote number', async () => {
    vi.spyOn(api, 'getUnderwritingQueue').mockResolvedValue(queue(items));
    const user = userEvent.setup();
    renderPage();
    await screen.findByRole('button', { name: /Q-2026-000101/ });

    const filters = screen.getByRole('group', { name: /filter by status/i });
    await user.click(within(filters).getByRole('button', { name: /follow-up/i }));
    expect(queueRows().map((row) => row.dataset.quoteNumber)).toEqual(['Q-2026-000102']);

    await user.click(within(filters).getByRole('button', { name: /^all/i }));
    await user.type(screen.getByLabelText(/search by quote number or applicant/i), 'BLAKE');
    expect(queueRows().map((row) => row.dataset.quoteNumber)).toEqual(['Q-2026-000103']);

    await user.clear(screen.getByLabelText(/search by quote number or applicant/i));
    await user.type(screen.getByLabelText(/search by quote number or applicant/i), 'nobody');
    expect(screen.getByText('No quotes match this filter.')).toBeInTheDocument();
  });

  it('marks the open quote for assistive technology', async () => {
    vi.spyOn(api, 'getUnderwritingQueue').mockResolvedValue(queue(items));
    vi.spyOn(api, 'getUnderwritingReview').mockResolvedValue(review());
    const user = userEvent.setup();
    renderPage();

    await openFirstQuote(user);

    expect(queueRows().map((row) => row.getAttribute('aria-current'))).toEqual(['true', null, null]);
  });

  it('says plainly when the queue cannot be loaded', async () => {
    vi.spyOn(api, 'getUnderwritingQueue').mockRejectedValue(
      new ApiError(503, { status: 503, title: 'Dependency unavailable', detail: 'Please retry.' }),
    );
    renderPage();

    expect(await screen.findByRole('alert')).toHaveTextContent('Dependency unavailable');
  });

  // =====================================================================
  // Review form
  // =====================================================================

  it('shows the application read-only, why it was referred, and carrier appetite', async () => {
    vi.spyOn(api, 'getUnderwritingQueue').mockResolvedValue(queue(items));
    vi.spyOn(api, 'getUnderwritingReview').mockResolvedValue(review());
    const user = userEvent.setup();
    renderPage();

    await openFirstQuote(user);

    const applicant = screen.getByLabelText(/insured name/i);
    expect(applicant).toHaveValue('Riley Morgan');
    expect(applicant).toHaveAttribute('readonly');
    expect(screen.getByLabelText(/annual mileage/i)).toHaveValue('12,000');
    expect(screen.getByLabelText(/house baseline premium/i)).toHaveValue('Not priced');
    expect(screen.getByText(/REFER: no verifiable prior insurance/)).toBeInTheDocument();
    expect(screen.getByText(/carriers would price this risk/).textContent).toMatch(/^3 of 5/);
  });

  it('requires a meaningful note before a follow-up or decline is sent', async () => {
    vi.spyOn(api, 'getUnderwritingQueue').mockResolvedValue(queue(items));
    vi.spyOn(api, 'getUnderwritingReview').mockResolvedValue(review());
    const decide = vi.spyOn(api, 'decideReferral');
    const user = userEvent.setup();
    renderPage();
    await openFirstQuote(user);

    await user.click(screen.getByRole('button', { name: /record decision/i }));
    expect(screen.getByText('Choose a decision.')).toBeInTheDocument();

    await user.click(screen.getByRole('radio', { name: /^request follow-up/i }));
    await user.click(screen.getByRole('button', { name: /send follow-up request/i }));
    expect(screen.getByText('Say what information is needed from the applicant.')).toBeInTheDocument();

    await user.click(screen.getByRole('radio', { name: /^decline/i }));
    await user.type(screen.getByLabelText(/reason for declining/i), 'No.');
    await user.click(screen.getByRole('button', { name: /decline quote/i }));
    expect(screen.getByText('Note must be at least 10 characters.')).toBeInTheDocument();

    expect(decide).not.toHaveBeenCalled();
  });

  it('only offers approval when a carrier would write the risk, and says why not', async () => {
    vi.spyOn(api, 'getUnderwritingQueue').mockResolvedValue(queue(items));
    vi.spyOn(api, 'getUnderwritingReview').mockResolvedValue(
      review({
        carrierAppetite: [
          { carrierId: 'liberty-crest', carrierName: 'Liberty Crest Insurance', willWrite: false, reason: 'Mileage.' },
          { carrierId: 'patriot-vale', carrierName: 'Patriot Vale Casualty', willWrite: false, reason: 'Mileage.' },
        ],
      }),
    );
    const user = userEvent.setup();
    renderPage();
    await openFirstQuote(user);

    expect(screen.getByRole('radio', { name: /^approve/i })).toBeDisabled();
    expect(screen.getByRole('radio', { name: /^request follow-up/i })).toBeEnabled();
    expect(screen.getByText(/no carrier on the panel will write this risk/i)).toBeInTheDocument();
  });

  // =====================================================================
  // Decisions
  // =====================================================================

  it('approves a referral, confirms it, and refreshes the queue', async () => {
    const getQueue = vi
      .spyOn(api, 'getUnderwritingQueue')
      .mockResolvedValueOnce(queue([items[0]]))
      .mockResolvedValue(queue([]));
    vi.spyOn(api, 'getUnderwritingReview').mockResolvedValue(review());
    const decide = vi.spyOn(api, 'decideReferral').mockResolvedValue(
      review({
        status: 'Rated',
        isAwaitingReview: false,
        decisionReason: undefined,
        reviewedBy: 'demo-underwriter (unauthenticated)',
        offerCount: 9,
        annualPremium: 1234.5,
        updatedUtc: '2026-09-13T12:00:00',
      }),
    );
    const user = userEvent.setup();
    renderPage();
    await openFirstQuote(user);

    await user.click(screen.getByRole('radio', { name: /^approve/i }));
    await user.click(screen.getByRole('button', { name: /approve quote/i }));

    await waitFor(() =>
      expect(decide).toHaveBeenCalledWith('quote-1', { decision: 'Approve', note: undefined }),
    );
    expect(
      await screen.findByText('Q-2026-000101 approved and rated: 9 carrier offers, best annual premium $1,234.50.'),
    ).toBeInTheDocument();
    expect(await screen.findByText('No quotes are awaiting review.')).toBeInTheDocument();
    expect(screen.getByText(/approved by demo-underwriter \(unauthenticated\)\./i)).toBeInTheDocument();
    expect(screen.queryByRole('radio')).not.toBeInTheDocument();
    expect(getQueue).toHaveBeenCalledTimes(2);
  });

  it('puts a server validation message beside the note until the note is edited', async () => {
    vi.spyOn(api, 'getUnderwritingQueue').mockResolvedValue(queue(items));
    vi.spyOn(api, 'getUnderwritingReview').mockResolvedValue(review());
    vi.spyOn(api, 'decideReferral').mockRejectedValue(
      new ApiError(400, {
        status: 400,
        title: 'One or more validation errors occurred',
        errors: { Note: ['The server rejected this note.'] },
      }),
    );
    const user = userEvent.setup();
    renderPage();
    await openFirstQuote(user);

    await user.click(screen.getByRole('radio', { name: /^request follow-up/i }));
    const note = screen.getByLabelText(/information needed from the applicant/i);
    await user.type(note, 'Upload proof of prior insurance.');
    await user.click(screen.getByRole('button', { name: /send follow-up request/i }));

    expect(await screen.findByText('The server rejected this note.')).toBeInTheDocument();
    expect(note).toHaveAttribute('aria-invalid', 'true');
    // Shown beside the field, not repeated as a general alert.
    expect(screen.queryByText('One or more validation errors occurred')).not.toBeInTheDocument();

    await user.type(note, ' Thanks.');
    expect(screen.queryByText('The server rejected this note.')).not.toBeInTheDocument();
  });

  it('tells the underwriter why a decision failed when someone else decided first', async () => {
    vi.spyOn(api, 'getUnderwritingQueue').mockResolvedValueOnce(queue(items)).mockResolvedValue(queue([]));
    vi.spyOn(api, 'getUnderwritingReview')
      .mockResolvedValueOnce(review())
      .mockResolvedValue(
        review({
          status: 'Declined',
          isAwaitingReview: false,
          reviewedBy: 'a.lindqvist',
          decisionReason: 'Declined by another underwriter.',
          updatedUtc: '2026-09-13T11:00:00',
        }),
      );
    vi.spyOn(api, 'decideReferral').mockRejectedValue(
      new ApiError(
        409,
        {
          status: 409,
          title: 'Conflicting operation',
          detail: 'Quote Q-2026-000101 is Declined and is no longer awaiting underwriter review. Refresh the queue.',
        },
        'corr-409',
      ),
    );
    const user = userEvent.setup();
    renderPage();
    await openFirstQuote(user);

    await user.click(screen.getByRole('radio', { name: /^decline/i }));
    await user.type(screen.getByLabelText(/reason for declining/i), 'Unable to verify any driving history.');
    await user.click(screen.getByRole('button', { name: /decline quote/i }));

    const alert = await screen.findByRole('alert');
    expect(alert).toHaveTextContent('Conflicting operation');
    expect(alert).toHaveTextContent('no longer awaiting underwriter review');
    expect(alert).toHaveTextContent('Reference: corr-409');
    expect(await screen.findByText(/declined by a\.lindqvist\./i)).toBeInTheDocument();
  });
});
