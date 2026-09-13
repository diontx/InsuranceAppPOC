import { HttpErrorResponse } from '@angular/common/http';

/**
 * A failed API call, normalised once so no component has to inspect an
 * `HttpErrorResponse`.
 *
 * The PolicyFlow API returns RFC 9457 ProblemDetails for every failure, so this is
 * the only error contract the module handles. The correlation id is kept because it
 * is what an underwriter reads out to support.
 */
export interface ApiProblem {
  /** HTTP status, or 0 when the request never reached the server. */
  status: number;
  title: string;
  detail?: string;
  /** Per-field validation messages, keyed by camelCase field name. */
  fieldErrors: Record<string, string>;
  correlationId?: string;
}

interface ProblemDetailsBody {
  title?: string;
  detail?: string;
  errors?: Record<string, string[]>;
  correlationId?: string;
}

export function toApiProblem(error: unknown): ApiProblem {
  if (!(error instanceof HttpErrorResponse)) {
    return {
      status: 0,
      title: 'Unexpected error',
      detail: error instanceof Error ? error.message : undefined,
      fieldErrors: {},
    };
  }

  if (error.status === 0) {
    // No response at all: API down, wrong base address, or CORS refused. Say so
    // rather than inventing a server message.
    return {
      status: 0,
      title: 'Cannot reach the PolicyFlow API',
      detail: 'Check that the API is running and that this page is allowed to call it.',
      fieldErrors: {},
      correlationId: undefined,
    };
  }

  // A proxy 502 or an IIS error page is not JSON. Do not let a parse miss hide the
  // real status code.
  const body: ProblemDetailsBody = isProblemDetails(error.error) ? error.error : {};

  const fieldErrors = Object.fromEntries(
    Object.entries(body.errors ?? {}).map(([field, messages]) => [camelCase(field), messages.join(' ')]),
  );

  return {
    status: error.status,
    title: body.title ?? (error.statusText || `Request failed with status ${error.status}`),
    detail: body.detail,
    fieldErrors,
    correlationId: body.correlationId ?? error.headers?.get('X-Correlation-Id') ?? undefined,
  };
}

function isProblemDetails(value: unknown): value is ProblemDetailsBody {
  return typeof value === 'object' && value !== null && ('title' in value || 'errors' in value);
}

/** The API reports C# property names (`Note`); form controls are camelCase. */
function camelCase(field: string): string {
  return field.length === 0 ? field : field.charAt(0).toLowerCase() + field.slice(1);
}
