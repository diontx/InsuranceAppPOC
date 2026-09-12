import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { ApiError, api, formatCurrency, formatNumber } from '../api/client';

/**
 * Request mapping and the error contract. These are the two places a client bug
 * would be invisible in the UI but wrong on the wire.
 */
describe('api client', () => {
  const fetchMock = vi.fn();

  beforeEach(() => {
    vi.stubGlobal('fetch', fetchMock);
    fetchMock.mockReset();
  });

  afterEach(() => {
    vi.unstubAllGlobals();
  });

  function jsonResponse(body: unknown, status = 200, headers: Record<string, string> = {}) {
    return new Response(JSON.stringify(body), {
      status,
      headers: { 'Content-Type': 'application/json', ...headers },
    });
  }

  it('posts a quote as JSON to the quotes endpoint', async () => {
    fetchMock.mockResolvedValue(
      jsonResponse({
        quoteId: 'q1',
        quoteNumber: 'Q-2026-000001',
        status: 'Pending',
        statusUrl: '/api/quotes/q1',
        correlationId: 'corr-1',
      }),
    );

    await api.submitQuote({
      customerName: 'Alex Johnson',
      state: 'GA',
      vehicleYear: 2024,
      vehicleMake: 'Toyota',
      vehicleModel: 'Camry',
      annualMileage: 12000,
      priorInsuranceMonths: 36,
    });

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe('/api/quotes');
    expect(init.method).toBe('POST');
    expect(init.headers['Content-Type']).toBe('application/json');
    expect(JSON.parse(init.body).customerName).toBe('Alex Johnson');
  });

  it('sends the idempotency key as a request header, not in the body', async () => {
    // The header is the contract. Putting the key in the body would mean the
    // server-side [FromHeader] binding never sees it.
    fetchMock.mockResolvedValue(
      jsonResponse({
        paymentId: 'p1',
        amount: 125,
        status: 'Approved',
        externalTransactionId: 'TXN-1',
        createdUtc: '2026-06-15T12:00:00Z',
        wasReplayed: false,
      }),
    );

    await api.submitPayment('pol1', 125, 'my-key-123');

    const [url, init] = fetchMock.mock.calls[0];
    expect(url).toBe('/api/policies/pol1/payments');
    expect(init.headers['Idempotency-Key']).toBe('my-key-123');
    expect(JSON.parse(init.body)).toEqual({ amount: 125 });
  });

  it('url-encodes lookup parameters', async () => {
    // A surname legitimately contains spaces, apostrophes and accents; an
    // unencoded value would corrupt the query string.
    fetchMock.mockResolvedValue(jsonResponse({ quote: {} }));

    await api.lookupQuote('Q-2026-000001', "O'Brien Smith");

    const [url] = fetchMock.mock.calls[0];
    expect(url).toContain('quoteNumber=Q-2026-000001');
    expect(url).toContain("lastName=O%27Brien+Smith");
  });

  it('throws an ApiError carrying the parsed problem details', async () => {
    // mockImplementation, not mockResolvedValue: a Response body can only be
    // read once, so a shared instance would be consumed by the first call.
    fetchMock.mockImplementation(() =>
      Promise.resolve(
        jsonResponse(
          {
            status: 422,
            title: 'Business rule violation',
            detail: 'Quote Q-2026-000001 is Pending.',
            correlationId: 'corr-99',
          },
          422,
        ),
      ),
    );

    await expect(api.getQuote('q1')).rejects.toBeInstanceOf(ApiError);

    try {
      await api.getQuote('q1');
    } catch (error) {
      const apiError = error as ApiError;
      expect(apiError.status).toBe(422);
      expect(apiError.message).toBe('Quote Q-2026-000001 is Pending.');
      expect(apiError.correlationId).toBe('corr-99');
      // 422 means the request was understood and rejected on business grounds;
      // retrying it unchanged cannot help.
      expect(apiError.isRetryable).toBe(false);
    }
  });

  it('camel-cases server field names so they match the form fields', async () => {
    fetchMock.mockResolvedValue(
      jsonResponse(
        {
          status: 400,
          title: 'Validation failed',
          errors: {
            CustomerName: ['Customer name is required.'],
            VehicleYear: ['Out of range.'],
          },
        },
        400,
      ),
    );

    try {
      await api.getQuote('q1');
    } catch (error) {
      expect((error as ApiError).fieldErrors).toEqual({
        customerName: 'Customer name is required.',
        vehicleYear: 'Out of range.',
      });
    }
  });

  it.each([429, 503, 500, 502])('marks %i as retryable', async (status) => {
    fetchMock.mockResolvedValue(jsonResponse({ status, title: 'Transient' }, status));

    try {
      await api.getQuote('q1');
    } catch (error) {
      expect((error as ApiError).isRetryable).toBe(true);
    }
  });

  it.each([400, 404, 409, 422])('marks %i as not retryable', async (status) => {
    fetchMock.mockResolvedValue(jsonResponse({ status, title: 'Permanent' }, status));

    try {
      await api.getQuote('q1');
    } catch (error) {
      expect((error as ApiError).isRetryable).toBe(false);
    }
  });

  it('falls back to the status code when the error body is not JSON', async () => {
    // A proxy 502 returns HTML. A parse failure must not mask the real status.
    fetchMock.mockResolvedValue(
      new Response('<html>Bad Gateway</html>', {
        status: 502,
        statusText: 'Bad Gateway',
        headers: { 'Content-Type': 'text/html' },
      }),
    );

    try {
      await api.getQuote('q1');
    } catch (error) {
      const apiError = error as ApiError;
      expect(apiError.status).toBe(502);
      expect(apiError.problem.title).toBe('Bad Gateway');
    }
  });

  it('captures the correlation id from the response header', async () => {
    fetchMock.mockResolvedValue(
      jsonResponse({ status: 500, title: 'Server error' }, 500, {
        'X-Correlation-Id': 'header-corr-id',
      }),
    );

    try {
      await api.getQuote('q1');
    } catch (error) {
      expect((error as ApiError).correlationId).toBe('header-corr-id');
    }
  });
});

describe('formatters', () => {
  it('formats currency to two decimal places', () => {
    expect(formatCurrency(1482.5)).toBe('$1,482.50');
    expect(formatCurrency(0.01)).toBe('$0.01');
  });

  it('renders an em dash for a missing value rather than $0.00', () => {
    // "$0.00" and "no premium yet" mean very different things on a quote screen.
    expect(formatCurrency(undefined)).toBe('—');
    expect(formatCurrency(null)).toBe('—');
  });

  it('formats numbers with thousands separators', () => {
    expect(formatNumber(12000)).toBe('12,000');
  });
});
