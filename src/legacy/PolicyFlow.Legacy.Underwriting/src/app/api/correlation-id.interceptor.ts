import { HttpInterceptorFn } from '@angular/common/http';

export const CORRELATION_ID_HEADER = 'X-Correlation-Id';

/**
 * Stamps every API call with its own correlation id.
 *
 * The API accepts a caller-supplied id and threads it through its logs, SQL and
 * the carrier fan-out, so an underwriter's "Approve" can be traced end to end from
 * the id shown in an error message. A functional interceptor rather than a header
 * set in each service method, so no call can be added that forgets it.
 */
export const correlationIdInterceptor: HttpInterceptorFn = (request, next) => {
  if (request.headers.has(CORRELATION_ID_HEADER)) {
    return next(request);
  }

  return next(request.clone({ setHeaders: { [CORRELATION_ID_HEADER]: newCorrelationId() } }));
};

function newCorrelationId(): string {
  // randomUUID needs a secure context; localhost qualifies, a plain-http intranet
  // host does not. The fallback is unique enough for tracing, not for security.
  return (
    globalThis.crypto?.randomUUID?.() ??
    `uw-${Date.now().toString(36)}-${Math.random().toString(36).slice(2, 10)}`
  );
}
