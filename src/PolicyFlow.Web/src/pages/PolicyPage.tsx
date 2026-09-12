import { useEffect, useState } from 'react';
import { Link, useParams } from 'react-router-dom';
import { ApiError, api, formatCurrency, formatDate } from '../api/client';
import type { PolicyView } from '../api/types';
import { Alert, ErrorAlert } from '../components/Alert';
import { StatusPill } from '../components/StatusPill';
import { usePaymentIntent } from '../hooks/usePaymentIntent';

/**
 * Bound policy, with the premium payment demo.
 *
 * The payment section is the point of this page. It deliberately exposes the
 * idempotency key in use and lets the user submit the same intent repeatedly, so
 * the duplicate-protection behaviour is observable rather than asserted.
 */
export function PolicyPage() {
  const { policyId } = useParams<{ policyId: string }>();
  const [policy, setPolicy] = useState<PolicyView | null>(null);
  const [loadError, setLoadError] = useState<Error | ApiError | null>(null);
  const [isLoading, setIsLoading] = useState(true);
  const [amount, setAmount] = useState('125.00');

  const {
    payment,
    isSubmitting,
    error: paymentError,
    idempotencyKey,
    attempts,
    submit,
    reset,
  } = usePaymentIntent(policyId);

  // Reloads the policy so TotalPaid and the payment list reflect what the server
  // actually persisted, rather than the client assuming its own success.
  useEffect(() => {
    if (!policyId) {
      return;
    }

    const controller = new AbortController();

    // isLoading is not set to true here, deliberately. It starts true and is
    // cleared in `finally`, so the initial load still shows a spinner — but the
    // refetch triggered by a successful payment keeps the existing policy on
    // screen instead of blanking it back to "Loading policy…".
    api
      .getPolicy(policyId, controller.signal)
      .then((result) => {
        setPolicy(result);
        setLoadError(null);
      })
      .catch((error: unknown) => {
        if (!(error instanceof DOMException)) {
          setLoadError(error as Error);
        }
      })
      .finally(() => setIsLoading(false));

    return () => controller.abort();
    // `payment` is a dependency on purpose: a successful payment should refresh
    // the policy so the balance shown comes from the server.
  }, [policyId, payment]);

  async function handlePay(event: React.FormEvent) {
    event.preventDefault();
    const parsed = Number(amount);

    if (!Number.isFinite(parsed) || parsed <= 0) {
      return;
    }

    await submit(parsed);
  }

  if (isLoading && !policy) {
    return (
      <div className="card">
        <div className="card__body row">
          <span className="spinner" aria-hidden="true" />
          <p>Loading policy…</p>
        </div>
      </div>
    );
  }

  if (loadError && !policy) {
    return <ErrorAlert error={loadError} />;
  }

  if (!policy) {
    return (
      <Alert variant="danger" title="Policy not found">
        No policy exists with that identifier.
      </Alert>
    );
  }

  return (
    <div className="stack stack--lg">
      <Alert variant="success" title="Policy bound">
        Policy <strong>{policy.policyNumber}</strong> is active with{' '}
        {policy.carrierName} on {policy.coverageTier} coverage.
      </Alert>

      {/* --- Policy summary -------------------------------------------- */}
      <section className="card">
        <div className="card__header">
          <div>
            <h1 style={{ fontSize: 'var(--text-xl)' }}>{policy.policyNumber}</h1>
            <p className="text-sm text-muted" style={{ marginTop: 'var(--space-1)' }}>
              {policy.insuredName} · {policy.state} · from quote{' '}
              <Link to={`/quotes/${policy.quoteId}`}>{policy.quoteNumber}</Link>
            </p>
          </div>
          <StatusPill status={policy.status} />
        </div>

        <div className="card__body stack stack--lg">
          <dl className="summary-list summary-list--wide">
            <div className="summary-item">
              <dt>Carrier</dt>
              <dd style={{ fontSize: 'var(--text-sm)' }}>{policy.carrierName}</dd>
            </div>
            <div className="summary-item">
              <dt>Coverage</dt>
              <dd style={{ fontSize: 'var(--text-sm)' }}>{policy.coverageTier}</dd>
            </div>
            <div className="summary-item">
              <dt>Deductible</dt>
              <dd>{formatCurrency(policy.deductible)}</dd>
            </div>
            <div className="summary-item">
              <dt>Term</dt>
              <dd style={{ fontSize: 'var(--text-sm)' }}>
                {formatDate(policy.effectiveDate)} — {formatDate(policy.expirationDate)}
              </dd>
            </div>
          </dl>

          <dl className="summary-list">
            <div className="summary-item">
              <dt>Annual premium</dt>
              <dd>{formatCurrency(policy.annualPremium)}</dd>
            </div>
            <div className="summary-item">
              <dt>Monthly instalment</dt>
              <dd>{formatCurrency(policy.monthlyPremium)}</dd>
            </div>
            <div className="summary-item">
              <dt>Paid to date</dt>
              <dd>{formatCurrency(policy.totalPaid)}</dd>
            </div>
            <div className="summary-item">
              <dt>Balance due</dt>
              <dd>{formatCurrency(policy.balanceDue)}</dd>
            </div>
          </dl>
        </div>
      </section>

      {/* --- Payment --------------------------------------------------- */}
      <section className="card">
        <div className="card__header">
          <h2>Make a payment</h2>
          <p className="text-xs text-muted">Simulated — no real transaction occurs</p>
        </div>

        <form className="card__body stack stack--lg" onSubmit={handlePay}>
          <Alert variant="info" title="What this demonstrates">
            <p>
              Submit the same payment twice. The second request carries the{' '}
              <strong>same idempotency key</strong>, so the API returns the original
              transaction instead of charging again — one database row, whatever the
              client does.
            </p>
            <p style={{ marginTop: 'var(--space-2)' }}>
              A disabled button would not achieve this. By the time a request has
              timed out it has already reached the server.
            </p>
          </Alert>

          <div className="form-grid">
            <div className="form-field">
              <label className="form-field__label" htmlFor="amount">
                Amount (USD)
              </label>
              <input
                id="amount"
                className="input"
                type="number"
                inputMode="decimal"
                min="0.01"
                max="100000"
                step="0.01"
                value={amount}
                onChange={(e) => setAmount(e.target.value)}
                disabled={isSubmitting}
              />
              <p className="form-field__hint">
                Monthly instalment is {formatCurrency(policy.monthlyPremium)}.
              </p>
            </div>

            {idempotencyKey && (
              <div className="form-field">
                <span className="form-field__label">Idempotency key in use</span>
                <p className="text-xs mono" style={{ overflowWrap: 'anywhere' }}>
                  {idempotencyKey}
                </p>
                <p className="form-field__hint">
                  Generated once for this payment intent and reused on every retry.
                  Attempts so far: {attempts}.
                </p>
              </div>
            )}
          </div>

          {paymentError && <ErrorAlert error={paymentError} />}

          {payment && (
            <Alert
              variant={payment.wasReplayed ? 'warning' : 'success'}
              title={
                payment.wasReplayed
                  ? 'Duplicate detected — original transaction returned'
                  : 'Payment approved'
              }
            >
              <p>
                {formatCurrency(payment.amount)} · {payment.status} · reference{' '}
                <span className="mono">{payment.externalTransactionId}</span>
              </p>
              {payment.wasReplayed && (
                <p style={{ marginTop: 'var(--space-2)' }}>
                  No second payment was created. This is the transaction recorded on
                  the first submission, returned unchanged.
                </p>
              )}
            </Alert>
          )}

          <div className="row">
            <button type="submit" className="btn btn--accent" disabled={isSubmitting}>
              {isSubmitting ? (
                <>
                  <span className="spinner" aria-hidden="true" />
                  Processing…
                </>
              ) : payment ? (
                'Submit the same payment again'
              ) : (
                'Pay now'
              )}
            </button>

            {payment && (
              <button
                type="button"
                className="btn btn--secondary"
                onClick={reset}
                disabled={isSubmitting}
              >
                Start a new payment
              </button>
            )}
          </div>

          {payment && (
            <p className="text-xs text-muted">
              "Submit the same payment again" reuses the key above — the correct
              behaviour for a retry. "Start a new payment" discards it, which is the
              only time a new key is appropriate.
            </p>
          )}
        </form>
      </section>

      {/* --- Payment history ------------------------------------------- */}
      {policy.payments.length > 0 && (
        <section>
          <div className="section-heading">
            <h2 style={{ fontSize: 'var(--text-lg)' }}>
              Payment history ({policy.payments.length})
            </h2>
          </div>

          <div className="table-scroll">
            <table className="data-table">
              <caption className="sr-only">
                Payments recorded against policy {policy.policyNumber}
              </caption>
              <thead>
                <tr>
                  <th scope="col">Date</th>
                  <th scope="col">Reference</th>
                  <th scope="col">Status</th>
                  <th scope="col" style={{ textAlign: 'right' }}>
                    Amount
                  </th>
                </tr>
              </thead>
              <tbody>
                {policy.payments.map((entry) => (
                  <tr key={entry.paymentId}>
                    <td>{formatDate(entry.createdUtc)}</td>
                    <td className="mono text-xs">{entry.externalTransactionId}</td>
                    <td>
                      <StatusPill status={entry.status} />
                    </td>
                    <td className="td--numeric" style={{ textAlign: 'right' }}>
                      {formatCurrency(entry.amount)}
                    </td>
                  </tr>
                ))}
              </tbody>
            </table>
          </div>

          <p className="text-xs text-muted" style={{ marginTop: 'var(--space-3)' }}>
            However many times a payment is submitted with the same idempotency key,
            exactly one row appears here.
          </p>
        </section>
      )}
    </div>
  );
}
