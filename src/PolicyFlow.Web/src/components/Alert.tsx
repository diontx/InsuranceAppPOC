import type { ReactNode } from 'react';
import { ApiError } from '../api/client';

interface AlertProps {
  variant: 'info' | 'success' | 'warning' | 'danger';
  title?: string;
  children: ReactNode;
}

export function Alert({ variant, title, children }: AlertProps) {
  return (
    <div
      className={`alert alert--${variant}`}
      // Errors and warnings are announced immediately; informational alerts are
      // announced politely so they do not interrupt what a screen reader user is
      // already hearing.
      role={variant === 'danger' ? 'alert' : 'status'}
    >
      <div>
        {title && <p className="alert__title">{title}</p>}
        <div>{children}</div>
      </div>
    </div>
  );
}

interface ErrorAlertProps {
  error: Error | ApiError;
  onRetry?: () => void;
}

/**
 * Renders an API failure usefully.
 *
 * Always surfaces the correlation id when there is one. That id is the whole
 * reason it is threaded through the API, SQL, the queue and the worker — a user
 * who can quote it turns "the site broke" into a single log query.
 */
export function ErrorAlert({ error, onRetry }: ErrorAlertProps) {
  const isApiError = error instanceof ApiError;
  const fieldErrors = isApiError ? error.fieldErrors : {};
  const hasFieldErrors = Object.keys(fieldErrors).length > 0;

  return (
    <Alert
      variant="danger"
      title={isApiError ? (error.problem.title ?? 'Request failed') : 'Something went wrong'}
    >
      <p>{error.message}</p>

      {hasFieldErrors && (
        <ul style={{ margin: 'var(--space-2) 0 0', paddingLeft: 'var(--space-5)' }}>
          {Object.entries(fieldErrors).map(([field, message]) => (
            <li key={field}>
              <strong>{field}:</strong> {message}
            </li>
          ))}
        </ul>
      )}

      {isApiError && error.correlationId && (
        <p className="text-xs mono" style={{ marginTop: 'var(--space-2)' }}>
          Reference: {error.correlationId}
        </p>
      )}

      {onRetry && (
        <button
          type="button"
          className="btn btn--secondary btn--sm"
          onClick={onRetry}
          style={{ marginTop: 'var(--space-3)' }}
        >
          Try again
        </button>
      )}
    </Alert>
  );
}
