import type {
  CarrierSummary,
  PaymentView,
  PolicyView,
  ProblemDetails,
  QuoteComparison,
  QuoteLookupResponse,
  QuoteStatusView,
  SubmitQuoteRequest,
  SubmitQuoteResponse,
} from './types';

/**
 * Base URL for the API.
 *
 * Defaults to a relative `/api`, which is what production uses: nginx serves the
 * SPA and reverse-proxies `/api` to the API container. A relative path means no
 * cross-origin request, no CORS preflight, and no build-time knowledge of the
 * API's hostname. `VITE_API_BASE_URL` overrides it for `npm run dev` against a
 * separately hosted API.
 */
const API_BASE = (import.meta.env.VITE_API_BASE_URL as string | undefined) ?? '/api';

/**
 * An API call that failed.
 *
 * Carries the parsed `ProblemDetails` so a caller can show per-field validation
 * messages, and the correlation id so a user can quote it to support — which is
 * the whole point of threading that id through the stack.
 */
export class ApiError extends Error {
  // Declared as fields rather than constructor parameter properties: the project
  // builds with `erasableSyntaxOnly`, which forbids syntax TypeScript cannot
  // simply strip.
  readonly status: number;
  readonly problem: ProblemDetails;
  readonly correlationId?: string;

  constructor(status: number, problem: ProblemDetails, correlationId?: string) {
    super(problem.detail ?? problem.title ?? `Request failed with status ${status}`);
    this.name = 'ApiError';
    this.status = status;
    this.problem = problem;
    this.correlationId = correlationId;
  }

  /** Per-field validation messages from a 400, flattened for display. */
  get fieldErrors(): Record<string, string> {
    if (!this.problem.errors) {
      return {};
    }

    return Object.fromEntries(
      Object.entries(this.problem.errors).map(([field, messages]) => [
        // The API returns PascalCase property names; the form uses camelCase.
        field.charAt(0).toLowerCase() + field.slice(1),
        messages.join(' '),
      ]),
    );
  }

  /** True when retrying the identical request could plausibly succeed. */
  get isRetryable(): boolean {
    return this.status === 429 || this.status === 503 || this.status >= 500;
  }
}

interface RequestOptions {
  method?: 'GET' | 'POST';
  body?: unknown;
  /** Extra request headers, e.g. `Idempotency-Key`. */
  headers?: Record<string, string>;
  signal?: AbortSignal;
}

/**
 * Single fetch wrapper for every call.
 *
 * Centralising it means the error contract, header handling and JSON parsing are
 * implemented once. Every non-2xx response becomes an `ApiError` carrying parsed
 * `ProblemDetails`, so no call site has to inspect `response.ok`.
 */
async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const { method = 'GET', body, headers = {}, signal } = options;

  const response = await fetch(`${API_BASE}${path}`, {
    method,
    headers: {
      Accept: 'application/json',
      ...(body !== undefined ? { 'Content-Type': 'application/json' } : {}),
      ...headers,
    },
    body: body !== undefined ? JSON.stringify(body) : undefined,
    signal,
  });

  // Echoed by the API's correlation middleware. Captured even on success so the
  // UI can display it, which is what makes a support call actionable.
  const correlationId = response.headers.get('X-Correlation-Id') ?? undefined;

  if (!response.ok) {
    let problem: ProblemDetails;

    try {
      problem = (await response.json()) as ProblemDetails;
    } catch {
      // A non-JSON error body: a proxy 502, or a network-level failure. Do not
      // let a parse error mask the real status code.
      problem = {
        status: response.status,
        title: response.statusText || 'Request failed',
        detail: 'The server returned an unexpected response.',
      };
    }

    throw new ApiError(response.status, problem, problem.correlationId ?? correlationId);
  }

  // 204 and 202-with-no-body would both fail `.json()`.
  if (response.status === 204 || response.headers.get('Content-Length') === '0') {
    return undefined as T;
  }

  return (await response.json()) as T;
}

export const api = {
  /** Submits a quote. Returns 202; poll `getQuote` for the outcome. */
  submitQuote(payload: SubmitQuoteRequest, signal?: AbortSignal): Promise<SubmitQuoteResponse> {
    return request<SubmitQuoteResponse>('/quotes', { method: 'POST', body: payload, signal });
  },

  /** Reads a quote's current underwriting status. Safe to poll. */
  getQuote(quoteId: string, signal?: AbortSignal): Promise<QuoteStatusView> {
    return request<QuoteStatusView>(`/quotes/${quoteId}`, { signal });
  },

  /** Reads the five-carrier comparison for a rated quote. */
  getQuoteOffers(quoteId: string, signal?: AbortSignal): Promise<QuoteComparison> {
    return request<QuoteComparison>(`/quotes/${quoteId}/offers`, { signal });
  },

  /**
   * Mobile self-service lookup by quote number and surname.
   *
   * Values are URL-encoded: a surname legitimately contains characters
   * (apostrophes, spaces, accents) that would otherwise corrupt the query string.
   */
  lookupQuote(
    quoteNumber: string,
    lastName: string,
    signal?: AbortSignal,
  ): Promise<QuoteLookupResponse> {
    const query = new URLSearchParams({ quoteNumber, lastName });
    return request<QuoteLookupResponse>(`/quotes/lookup?${query}`, { signal });
  },

  /** Lists the carrier panel. Used on the landing page. */
  getCarriers(signal?: AbortSignal): Promise<CarrierSummary[]> {
    return request<CarrierSummary[]>('/carriers', { signal });
  },

  /** Binds a rated quote into a policy on the selected offer. */
  createPolicy(
    quoteId: string,
    offerId: string | undefined,
    signal?: AbortSignal,
  ): Promise<PolicyView> {
    return request<PolicyView>(`/policies/from-quote/${quoteId}`, {
      method: 'POST',
      body: { offerId },
      signal,
    });
  },

  getPolicy(policyId: string, signal?: AbortSignal): Promise<PolicyView> {
    return request<PolicyView>(`/policies/${policyId}`, { signal });
  },

  /**
   * Takes a premium payment.
   *
   * `idempotencyKey` MUST be one key per payment *intent*, reused on every retry
   * of that intent. Generating a fresh key per attempt would defeat the
   * protection entirely: the server would see each retry as a new payment.
   */
  submitPayment(
    policyId: string,
    amount: number,
    idempotencyKey: string,
    signal?: AbortSignal,
  ): Promise<PaymentView> {
    return request<PaymentView>(`/policies/${policyId}/payments`, {
      method: 'POST',
      body: { amount },
      headers: { 'Idempotency-Key': idempotencyKey },
      signal,
    });
  },
};

/** Formats a number as USD. */
export function formatCurrency(value: number | undefined | null): string {
  if (value === undefined || value === null) {
    return '—';
  }

  return new Intl.NumberFormat('en-US', {
    style: 'currency',
    currency: 'USD',
    minimumFractionDigits: 2,
    maximumFractionDigits: 2,
  }).format(value);
}

/** Formats a number with thousands separators. */
export function formatNumber(value: number): string {
  return new Intl.NumberFormat('en-US').format(value);
}

/** Formats an ISO date or date-time as a short local date. */
export function formatDate(value: string | undefined): string {
  if (!value) {
    return '—';
  }

  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }

  return new Intl.DateTimeFormat('en-US', {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
  }).format(date);
}
