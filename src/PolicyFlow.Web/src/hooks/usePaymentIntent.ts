import { useCallback, useRef, useState } from 'react';
import { ApiError, api } from '../api/client';
import type { PaymentView } from '../api/types';

/**
 * Generates an idempotency key.
 *
 * Prefers `crypto.randomUUID`, falling back to `getRandomValues` for older
 * browsers. The fallback still uses the CSPRNG rather than `Math.random`: a
 * predictable key is worse than no key, because a collision between two
 * customers' keys would make one payment silently replay the other's.
 */
function newIdempotencyKey(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }

  if (typeof crypto !== 'undefined' && typeof crypto.getRandomValues === 'function') {
    const bytes = crypto.getRandomValues(new Uint8Array(16));
    return Array.from(bytes, (b) => b.toString(16).padStart(2, '0')).join('');
  }

  throw new Error(
    'No cryptographic random source is available, so a safe idempotency key ' +
      'cannot be generated. Refusing to submit a payment without one.',
  );
}

export interface PaymentIntentState {
  payment: PaymentView | null;
  isSubmitting: boolean;
  error: ApiError | Error | null;
  /** The key in use for the current intent. Shown in the demo. */
  idempotencyKey: string | null;
  /** Attempts made for the current intent, including retries. */
  attempts: number;
  /** Submits, or retries, the current payment intent. */
  submit: (amount: number) => Promise<void>;
  /** Starts a genuinely new payment, discarding the previous key. */
  reset: () => void;
}

/**
 * Manages one payment *intent* against a policy.
 *
 * The important behaviour: **one key per intent, reused on every retry.** The key
 * is generated on the first submit and held in a ref for the life of the intent,
 * so pressing "Retry" after a timeout sends the identical key. The server then
 * returns the original transaction instead of charging twice.
 *
 * The naive alternative — a fresh key per HTTP attempt — looks like it works and
 * defeats the protection completely, because the server sees every retry as a new
 * payment. That is the specific mistake this hook exists to prevent.
 *
 * Disabling the submit button is not a substitute. By the time a request has
 * timed out, it has already reached the server; the button never had a say.
 */
export function usePaymentIntent(policyId: string | undefined): PaymentIntentState {
  const [payment, setPayment] = useState<PaymentView | null>(null);
  const [isSubmitting, setIsSubmitting] = useState(false);
  const [error, setError] = useState<ApiError | Error | null>(null);
  const [attempts, setAttempts] = useState(0);

  // A ref, not state: it must survive re-renders without being recreated, and
  // reading it must not depend on a render having flushed.
  const keyRef = useRef<string | null>(null);
  const [visibleKey, setVisibleKey] = useState<string | null>(null);

  const submit = useCallback(
    async (amount: number) => {
      if (!policyId) {
        setError(new Error('No policy selected.'));
        return;
      }

      // Generate once, on the first attempt for this intent. Every later call —
      // including an explicit retry after a failure — reuses it.
      if (!keyRef.current) {
        try {
          keyRef.current = newIdempotencyKey();
          setVisibleKey(keyRef.current);
        } catch (keyError) {
          setError(keyError as Error);
          return;
        }
      }

      setIsSubmitting(true);
      setError(null);
      setAttempts((count) => count + 1);

      try {
        const result = await api.submitPayment(policyId, amount, keyRef.current);
        setPayment(result);
      } catch (submitError) {
        // The key is deliberately NOT cleared here. A failed attempt may still
        // have been processed server-side (a lost response looks identical to a
        // lost request), so the retry must carry the same key to collapse onto
        // the original transaction rather than creating a second one.
        setError(submitError as Error);
      } finally {
        setIsSubmitting(false);
      }
    },
    [policyId],
  );

  /**
   * Clears the key so the next submit is a new payment.
   *
   * Only correct when the user genuinely intends a *second* payment. Calling it
   * to "retry" would reintroduce the duplicate-charge bug.
   */
  const reset = useCallback(() => {
    keyRef.current = null;
    setVisibleKey(null);
    setPayment(null);
    setError(null);
    setAttempts(0);
  }, []);

  return {
    payment,
    isSubmitting,
    error,
    idempotencyKey: visibleKey,
    attempts,
    submit,
    reset,
  };
}
