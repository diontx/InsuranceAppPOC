import { MemoryRouter } from 'react-router-dom';
import { render, screen, waitFor, within } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, api } from '../api/client';
import { loadAgentSession, saveAgentSession } from '../auth/agentSession';
import { AgentPage } from '../pages/AgentPage';
import { quoteView } from './agentFixtures';

/** The agent workspace: sign-in, the quote list, and submitting a quote. */
describe('AgentPage', () => {
  afterEach(() => sessionStorage.clear());

  function renderPage() {
    return render(
      <MemoryRouter>
        <AgentPage />
      </MemoryRouter>,
    );
  }

  function signedInAs(agentId = 'a.patel') {
    saveAgentSession({ agentId, accessToken: 'token-123', expiresAtMs: Date.now() + 3_600_000 });
  }

  // =====================================================================
  // Sign-in
  // =====================================================================

  it('signs an agent in and loads their quotes', async () => {
    const issueDevToken = vi.spyOn(api, 'issueDevToken').mockResolvedValue({
      accessToken: 'token-abc',
      tokenType: 'Bearer',
      expiresInSeconds: 3600,
      role: 'Agent',
      scopes: ['quotes:write'],
      warning: 'Development token.',
    });
    const getMyQuotes = vi.spyOn(api, 'getMyQuotes').mockResolvedValue({ submittedBy: 'a.patel', items: [] });
    const user = userEvent.setup();
    renderPage();

    await user.type(screen.getByLabelText(/agent id/i), '  a.patel ');
    await user.click(screen.getByRole('button', { name: /sign in/i }));

    expect(await screen.findByText(/signed in as/i)).toHaveTextContent('Signed in as a.patel');
    expect(issueDevToken).toHaveBeenCalledWith('Agent', 'a.patel');
    expect(loadAgentSession()?.accessToken).toBe('token-abc');
    expect(await screen.findByText(/you have not submitted any quotes yet/i)).toBeInTheDocument();
    expect(getMyQuotes).toHaveBeenCalled();
  });

  it('rejects an invalid agent ID without calling the identity provider', async () => {
    const issueDevToken = vi.spyOn(api, 'issueDevToken');
    const user = userEvent.setup();
    renderPage();

    await user.type(screen.getByLabelText(/agent id/i), 'bad id!');
    await user.click(screen.getByRole('button', { name: /sign in/i }));

    expect(screen.getByText(/enter your agent id/i)).toBeInTheDocument();
    expect(issueDevToken).not.toHaveBeenCalled();
  });

  it('explains when the development identity provider is turned off', async () => {
    vi.spyOn(api, 'issueDevToken').mockRejectedValue(new ApiError(404, { status: 404, title: 'Not Found' }));
    const user = userEvent.setup();
    renderPage();

    await user.type(screen.getByLabelText(/agent id/i), 'a.patel');
    await user.click(screen.getByRole('button', { name: /sign in/i }));

    expect(await screen.findByRole('alert')).toHaveTextContent(/identity provider is turned off/i);
    expect(loadAgentSession()).toBeNull();
  });

  it('restores a sign-in from this browser session', async () => {
    signedInAs('m.okafor');
    vi.spyOn(api, 'getMyQuotes').mockResolvedValue({ submittedBy: 'm.okafor', items: [] });

    renderPage();

    expect(await screen.findByText(/signed in as/i)).toHaveTextContent('m.okafor');
  });

  it('signs out', async () => {
    signedInAs();
    vi.spyOn(api, 'getMyQuotes').mockResolvedValue({ submittedBy: 'a.patel', items: [] });
    const user = userEvent.setup();
    renderPage();

    await user.click(await screen.findByRole('button', { name: /sign out/i }));

    expect(screen.getByRole('heading', { name: /agent sign-in/i })).toBeInTheDocument();
    expect(loadAgentSession()).toBeNull();
  });

  // =====================================================================
  // My quotes
  // =====================================================================

  it('lists the agent quotes with status, outcome and a link to each', async () => {
    signedInAs();
    vi.spyOn(api, 'getMyQuotes').mockResolvedValue({
      submittedBy: 'a.patel',
      items: [
        quoteView({
          quoteId: 'q-referred',
          quoteNumber: 'Q-2026-000102',
          customerName: 'Sam Carter',
          status: 'FollowUp',
          annualPremium: undefined,
          offerCount: 0,
          decisionReason: 'Upload proof of prior insurance.',
        }),
        quoteView({ quoteId: 'q-rated', quoteNumber: 'Q-2026-000101', customerName: 'Dana Reyes' }),
      ],
    });
    renderPage();

    const table = await screen.findByRole('table', { name: /quotes you submitted/i });
    const rows = within(table).getAllByRole('row').slice(1);

    expect(rows[0]).toHaveTextContent('Q-2026-000102');
    expect(rows[0]).toHaveTextContent('Follow-up');
    expect(rows[0]).toHaveTextContent('Upload proof of prior insurance.');

    expect(rows[1]).toHaveTextContent('Dana Reyes');
    expect(rows[1]).toHaveTextContent('$812.50');
    expect(rows[1]).toHaveTextContent('15 offers');
    expect(within(rows[1]).getByRole('link', { name: /view q-2026-000101/i })).toHaveAttribute(
      'href',
      '/quotes/q-rated',
    );

    expect(screen.getByText('With an underwriter').nextElementSibling).toHaveTextContent('1');
  });

  it('offers to sign in again when the token has expired', async () => {
    signedInAs();
    vi.spyOn(api, 'getMyQuotes').mockRejectedValue(new ApiError(401, { status: 401, title: 'Sign-in required' }));
    const user = userEvent.setup();
    renderPage();

    expect(await screen.findByText(/your sign-in has expired/i)).toBeInTheDocument();
    await user.click(screen.getByRole('button', { name: /sign in again/i }));

    expect(screen.getByRole('heading', { name: /agent sign-in/i })).toBeInTheDocument();
  });

  // =====================================================================
  // Submitting
  // =====================================================================

  it('submits a quote as the agent, confirms it, clears the form and refreshes the list', async () => {
    signedInAs();
    const getMyQuotes = vi
      .spyOn(api, 'getMyQuotes')
      .mockResolvedValueOnce({ submittedBy: 'a.patel', items: [] })
      .mockResolvedValue({
        submittedBy: 'a.patel',
        items: [quoteView({ quoteNumber: 'Q-2026-000200', customerName: 'Jamie Lee' })],
      });
    const submitQuote = vi.spyOn(api, 'submitQuote').mockResolvedValue({
      quoteId: 'q-200',
      quoteNumber: 'Q-2026-000200',
      status: 'Pending',
      statusUrl: '/api/quotes/q-200',
      correlationId: 'corr-200',
    });
    const user = userEvent.setup();
    renderPage();
    await screen.findByText(/you have not submitted any quotes yet/i);

    await user.type(screen.getByLabelText(/full name/i), 'Jamie Lee');
    await user.type(screen.getByLabelText(/^make$/i), 'Subaru');
    await user.type(screen.getByLabelText(/^model$/i), 'Outback');
    await user.click(screen.getByRole('button', { name: /submit quote/i }));

    await waitFor(() =>
      expect(submitQuote).toHaveBeenCalledWith(
        expect.objectContaining({ customerName: 'Jamie Lee', vehicleMake: 'Subaru' }),
        undefined,
        true,
      ),
    );
    expect(await screen.findByText('Q-2026-000200 submitted')).toBeInTheDocument();
    expect(screen.getByLabelText(/full name/i)).toHaveValue('');
    expect(await screen.findByRole('table', { name: /quotes you submitted/i })).toHaveTextContent('Jamie Lee');
    expect(getMyQuotes).toHaveBeenCalledTimes(2);
  });
});
