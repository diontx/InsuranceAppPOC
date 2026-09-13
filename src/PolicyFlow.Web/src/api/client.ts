import { loadAgentSession } from '../auth/agentSession';
import type {
  AgentQuotesResponse,
  CarrierSummary,
  DevTokenResponse,
  PaymentView,
  PolicyView,
  ProblemDetails,
  QuoteComparison,
  QuoteLookupResponse,
  QuoteStatusView,
  ReviewDecisionRequest,
  SubmitQuoteRequest,
  SubmitQuoteResponse,
  UnderwritingQueue,
  UnderwritingReview,
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
  /**
   * Send the signed-in agent's bearer token. Opt-in per call, so the customer and
   * underwriter pages stay anonymous even while an agent is signed in on the same
   * browser — otherwise an underwriting decision would be recorded as the agent's.
   */
  asAgent?: boolean;
}

/**
 * Single fetch wrapper for every call.
 *
 * Centralising it means the error contract, header handling and JSON parsing are
 * implemented once. Every non-2xx response becomes an `ApiError` carrying parsed
 * `ProblemDetails`, so no call site has to inspect `response.ok`.
 */
async function request<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const { method = 'GET', body, headers = {}, signal, asAgent = false } = options;
  const accessToken = asAgent ? loadAgentSession()?.accessToken : undefined;

  const response = await fetch(`${API_BASE}${path}`, {
    method,
    headers: {
      Accept: 'application/json',
      ...(accessToken ? { Authorization: `Bearer ${accessToken}` } : {}),
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
  /**
   * Submits a quote. Returns 202; poll `getQuote` for the outcome. With `asAgent`
   * the signed-in agent's token goes too, and the API attributes the quote to them.
   */
  submitQuote(
    payload: SubmitQuoteRequest,
    signal?: AbortSignal,
    asAgent = false,
  ): Promise<SubmitQuoteResponse> {
    return request<SubmitQuoteResponse>('/quotes', { method: 'POST', body: payload, signal, asAgent });
  },

  /** The signed-in agent's quotes, newest first. 401 when not signed in. */
  getMyQuotes(signal?: AbortSignal): Promise<AgentQuotesResponse> {
    return request<AgentQuotesResponse>('/quotes/mine', { signal, asAgent: true });
  },

  /**
   * Signs in through the SIMULATED identity provider: issues a token for a role and
   * subject with no credential check. Development only — the API returns 404
   * anywhere else. Production replaces this call with an Entra ID redirect.
   */
  issueDevToken(role: 'Agent', subject: string, signal?: AbortSignal): Promise<DevTokenResponse> {
    return request<DevTokenResponse>('/auth/dev-token', {
      method: 'POST',
      body: { role, subject },
      signal,
    });
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

  /** Quotes awaiting an underwriter, oldest first. */
  getUnderwritingQueue(signal?: AbortSignal): Promise<UnderwritingQueue> {
    return request<UnderwritingQueue>('/underwriting/queue', { signal });
  },

  /** One quote with its eligibility trace and carrier appetite. */
  getUnderwritingReview(quoteId: string, signal?: AbortSignal): Promise<UnderwritingReview> {
    return request<UnderwritingReview>(`/underwriting/quotes/${encodeURIComponent(quoteId)}`, {
      signal,
    });
  },

  /**
   * Records an underwriter decision. There is deliberately no underwriter field:
   * the API takes the decision-maker from the token, never from the body.
   */
  decideReferral(
    quoteId: string,
    decision: ReviewDecisionRequest,
    signal?: AbortSignal,
  ): Promise<UnderwritingReview> {
    return request<UnderwritingReview>(
      `/underwriting/quotes/${encodeURIComponent(quoteId)}/decision`,
      { method: 'POST', body: decision, signal },
    );
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

/**
 * Parses an API `...Utc` timestamp as UTC.
 *
 * SQL Server `datetime2` carries no offset, so these serialise WITHOUT a trailing
 * `Z`, and `new Date('2026-09-13T10:00:00')` would read them as local time —
 * shifting every "time in queue" by the browser's UTC offset. The field name is
 * the contract, so a missing designator means UTC.
 */
export function parseUtc(value: string | undefined | null): Date | null {
  if (!value) {
    return null;
  }

  const hasOffset = /(?:[zZ]|[+-]\d{2}:?\d{2})$/.test(value);
  const date = new Date(hasOffset ? value : `${value}Z`);

  return Number.isNaN(date.getTime()) ? null : date;
}

/** Formats an API UTC timestamp as a short local date and time. */
export function formatDateTime(value: string | undefined): string {
  const date = parseUtc(value);
  if (!date) {
    return '—';
  }

  return new Intl.DateTimeFormat('en-US', {
    year: 'numeric',
    month: 'short',
    day: 'numeric',
    hour: 'numeric',
    minute: '2-digit',
  }).format(date);
}

/** How long a quote has waited, e.g. `3h 5m`. `nowMs` is passed in so it is testable. */
export function formatTimeInQueue(sinceUtc: string | undefined, nowMs: number): string {
  const since = parseUtc(sinceUtc);
  if (!since) {
    return '—';
  }

  // Clamped: a browser clock slightly behind the server must not show a negative wait.
  const minutes = Math.max(0, Math.floor((nowMs - since.getTime()) / 60_000));

  if (minutes < 1) {
    return 'just now';
  }

  if (minutes < 60) {
    return `${minutes}m`;
  }

  const hours = Math.floor(minutes / 60);
  if (hours < 24) {
    return minutes % 60 === 0 ? `${hours}h` : `${hours}h ${minutes % 60}m`;
  }

  const days = Math.floor(hours / 24);
  return hours % 24 === 0 ? `${days}d` : `${days}d ${hours % 24}h`;
}

/** Underwriting language for quote statuses; unknown values pass through. */
export function formatStatusLabel(status: string): string {
  switch (status) {
    case 'FollowUp':
      return 'Follow-up';
    case 'Rated':
      return 'Approved';
    default:
      return status;
  }
}
