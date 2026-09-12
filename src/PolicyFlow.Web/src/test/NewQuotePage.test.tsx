import { MemoryRouter } from 'react-router-dom';
import { render, screen, waitFor } from '@testing-library/react';
import userEvent from '@testing-library/user-event';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, api } from '../api/client';
import { NewQuotePage } from '../pages/NewQuotePage';

const navigate = vi.fn();

vi.mock('react-router-dom', async () => {
  const actual = await vi.importActual<typeof import('react-router-dom')>('react-router-dom');
  return { ...actual, useNavigate: () => navigate };
});

/** Quote form validation and request mapping. */
describe('NewQuotePage', () => {
  beforeEach(() => {
    vi.restoreAllMocks();
    navigate.mockClear();
  });

  function renderPage() {
    return render(
      <MemoryRouter>
        <NewQuotePage />
      </MemoryRouter>,
    );
  }

  it('rejects an empty name without calling the API', async () => {
    const submitQuote = vi.spyOn(api, 'submitQuote');
    const user = userEvent.setup();
    renderPage();

    await user.click(screen.getByRole('button', { name: /compare carriers/i }));

    expect(await screen.findByText(/enter the name to be insured/i)).toBeInTheDocument();
    expect(submitQuote).not.toHaveBeenCalled();
  });

  it('rejects a name containing markup', async () => {
    // Client-side convenience, not a security boundary — the API validates
    // independently — but it should still catch obviously wrong input.
    const submitQuote = vi.spyOn(api, 'submitQuote');
    const user = userEvent.setup();
    renderPage();

    await user.type(screen.getByLabelText(/full name/i), '<script>alert(1)</script>');
    await user.type(screen.getByLabelText(/^make$/i), 'Toyota');
    await user.type(screen.getByLabelText(/^model$/i), 'Camry');
    await user.click(screen.getByRole('button', { name: /compare carriers/i }));

    expect(await screen.findByText(/letters, spaces, hyphens/i)).toBeInTheDocument();
    expect(submitQuote).not.toHaveBeenCalled();
  });

  it('rejects an out-of-range vehicle year', async () => {
    const submitQuote = vi.spyOn(api, 'submitQuote');
    const user = userEvent.setup();
    renderPage();

    await user.type(screen.getByLabelText(/full name/i), 'Alex Johnson');
    await user.type(screen.getByLabelText(/^make$/i), 'Toyota');
    await user.type(screen.getByLabelText(/^model$/i), 'Camry');

    const yearField = screen.getByLabelText(/model year/i);
    await user.clear(yearField);
    await user.type(yearField, '1960');

    await user.click(screen.getByRole('button', { name: /compare carriers/i }));

    expect(await screen.findByText(/enter a year between 1981/i)).toBeInTheDocument();
    expect(submitQuote).not.toHaveBeenCalled();
  });

  it('maps the form to the API request with numeric coercion and trimming', async () => {
    const submitQuote = vi.spyOn(api, 'submitQuote').mockResolvedValue({
      quoteId: 'c0ffee00-0000-4000-8000-000000000003',
      quoteNumber: 'Q-2026-000001',
      status: 'Pending',
      statusUrl: '/api/quotes/c0ffee00-0000-4000-8000-000000000003',
      correlationId: 'corr-1',
    });

    const user = userEvent.setup();
    renderPage();

    await user.type(screen.getByLabelText(/full name/i), '  Alex Johnson  ');
    await user.selectOptions(screen.getByLabelText(/garaging state/i), 'FL');
    await user.type(screen.getByLabelText(/^make$/i), ' Toyota ');
    await user.type(screen.getByLabelText(/^model$/i), ' Camry ');

    const mileage = screen.getByLabelText(/annual mileage/i);
    await user.clear(mileage);
    await user.type(mileage, '18000');

    await user.click(screen.getByRole('button', { name: /compare carriers/i }));

    await waitFor(() => expect(submitQuote).toHaveBeenCalledTimes(1));

    // Strings become numbers and whitespace is trimmed before the request leaves.
    expect(submitQuote.mock.calls[0][0]).toEqual({
      customerName: 'Alex Johnson',
      state: 'FL',
      vehicleYear: new Date().getFullYear() - 2,
      vehicleMake: 'Toyota',
      vehicleModel: 'Camry',
      annualMileage: 18000,
      priorInsuranceMonths: 36,
    });
  });

  it('navigates to the status page on a 202', async () => {
    vi.spyOn(api, 'submitQuote').mockResolvedValue({
      quoteId: 'c0ffee00-0000-4000-8000-000000000004',
      quoteNumber: 'Q-2026-000002',
      status: 'Pending',
      statusUrl: '/api/quotes/c0ffee00-0000-4000-8000-000000000004',
      correlationId: 'corr-2',
    });

    const user = userEvent.setup();
    renderPage();

    await user.type(screen.getByLabelText(/full name/i), 'Dana Reyes');
    await user.type(screen.getByLabelText(/^make$/i), 'Honda');
    await user.type(screen.getByLabelText(/^model$/i), 'Accord');
    await user.click(screen.getByRole('button', { name: /compare carriers/i }));

    await waitFor(() =>
      expect(navigate).toHaveBeenCalledWith('/quotes/c0ffee00-0000-4000-8000-000000000004'),
    );
  });

  it('shows server-side field errors from a 400', async () => {
    // The API is the authority on validation; its per-field messages must reach
    // the form rather than being flattened into one generic error.
    vi.spyOn(api, 'submitQuote').mockRejectedValue(
      new ApiError(400, {
        status: 400,
        title: 'One or more validation errors occurred',
        errors: { State: ['State must be one of: GA, FL, TX.'] },
      }),
    );

    const user = userEvent.setup();
    renderPage();

    await user.type(screen.getByLabelText(/full name/i), 'Alex Johnson');
    await user.type(screen.getByLabelText(/^make$/i), 'Toyota');
    await user.type(screen.getByLabelText(/^model$/i), 'Camry');
    await user.click(screen.getByRole('button', { name: /compare carriers/i }));

    expect(await screen.findByText(/state must be one of/i)).toBeInTheDocument();
  });

  it('accepts the FAIL-DEPENDENCY demo trigger', async () => {
    // The name validator must not block the demo hook for retry behaviour:
    // uppercase letters and hyphens are legitimate in real names too.
    const submitQuote = vi.spyOn(api, 'submitQuote').mockResolvedValue({
      quoteId: 'c0ffee00-0000-4000-8000-000000000005',
      quoteNumber: 'Q-2026-000003',
      status: 'Pending',
      statusUrl: '/api/quotes/c0ffee00-0000-4000-8000-000000000005',
      correlationId: 'corr-3',
    });

    const user = userEvent.setup();
    renderPage();

    await user.type(screen.getByLabelText(/full name/i), 'FAIL-DEPENDENCY');
    await user.type(screen.getByLabelText(/^make$/i), 'Toyota');
    await user.type(screen.getByLabelText(/^model$/i), 'Camry');
    await user.click(screen.getByRole('button', { name: /compare carriers/i }));

    await waitFor(() => expect(submitQuote).toHaveBeenCalledTimes(1));
    expect(submitQuote.mock.calls[0][0].customerName).toBe('FAIL-DEPENDENCY');
  });
});
