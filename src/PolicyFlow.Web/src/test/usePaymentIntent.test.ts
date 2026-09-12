import { act } from 'react';
import { renderHook, waitFor } from '@testing-library/react';
import { beforeEach, describe, expect, it, vi } from 'vitest';
import { api } from '../api/client';
import { ApiError } from '../api/client';
import { usePaymentIntent } from '../hooks/usePaymentIntent';
import type { PaymentView } from '../api/types';

/**
 * These tests exist for one reason: the idempotency contract is only worth
 * anything if a retry reuses the key. A hook that generated a fresh key per
 * attempt would look identical in the UI and silently double-charge, so the key
 * reuse is asserted directly rather than inferred.
 */
describe('usePaymentIntent', () => {
  const POLICY_ID = 'c0ffee00-0000-4000-8000-000000000001';

  function payment(overrides: Partial<PaymentView> = {}): PaymentView {
    return {
      paymentId: 'p1',
      amount: 125,
      status: 'Approved',
      externalTransactionId: 'TXN-SIM-ABCD1234',
      createdUtc: '2026-06-15T12:00:00Z',
      wasReplayed: false,
      ...overrides,
    };
  }

  beforeEach(() => {
    vi.restoreAllMocks();
  });

  it('generates an idempotency key on the first submit', async () => {
    const submitPayment = vi.spyOn(api, 'submitPayment').mockResolvedValue(payment());

    const { result } = renderHook(() => usePaymentIntent(POLICY_ID));

    expect(result.current.idempotencyKey).toBeNull();

    await act(async () => {
      await result.current.submit(125);
    });

    expect(result.current.idempotencyKey).toBeTruthy();
    expect(submitPayment).toHaveBeenCalledTimes(1);
    // The third argument is the key, which must have been sent.
    expect(submitPayment.mock.calls[0][2]).toBe(result.current.idempotencyKey);
  });

  it('reuses the same key when the same intent is submitted again', async () => {
    const submitPayment = vi
      .spyOn(api, 'submitPayment')
      .mockResolvedValueOnce(payment())
      .mockResolvedValueOnce(payment({ wasReplayed: true }));

    const { result } = renderHook(() => usePaymentIntent(POLICY_ID));

    await act(async () => {
      await result.current.submit(125);
    });
    const firstKey = result.current.idempotencyKey;

    await act(async () => {
      await result.current.submit(125);
    });

    expect(submitPayment).toHaveBeenCalledTimes(2);
    // THE assertion. Both requests carried the identical key, so the server
    // collapses the second onto the first.
    expect(submitPayment.mock.calls[0][2]).toBe(firstKey);
    expect(submitPayment.mock.calls[1][2]).toBe(firstKey);
    expect(result.current.idempotencyKey).toBe(firstKey);
  });

  it('surfaces the replayed flag so the UI can prove no second charge occurred', async () => {
    vi.spyOn(api, 'submitPayment').mockResolvedValue(payment({ wasReplayed: true }));

    const { result } = renderHook(() => usePaymentIntent(POLICY_ID));

    await act(async () => {
      await result.current.submit(125);
    });

    expect(result.current.payment?.wasReplayed).toBe(true);
  });

  it('keeps the key after a failure, so a retry cannot create a second payment', async () => {
    // A failed request may still have been processed server-side: a lost response
    // is indistinguishable from a lost request. Clearing the key here would be
    // the classic double-charge bug.
    const submitPayment = vi
      .spyOn(api, 'submitPayment')
      .mockRejectedValueOnce(
        new ApiError(503, { status: 503, title: 'Dependency unavailable' }),
      )
      .mockResolvedValueOnce(payment({ wasReplayed: true }));

    const { result } = renderHook(() => usePaymentIntent(POLICY_ID));

    await act(async () => {
      await result.current.submit(125);
    });

    const keyAfterFailure = result.current.idempotencyKey;
    expect(result.current.error).toBeInstanceOf(ApiError);
    expect(keyAfterFailure).toBeTruthy();

    await act(async () => {
      await result.current.submit(125);
    });

    expect(submitPayment.mock.calls[1][2]).toBe(keyAfterFailure);
    expect(result.current.payment?.wasReplayed).toBe(true);
    expect(result.current.error).toBeNull();
  });

  it('counts attempts across retries of one intent', async () => {
    vi.spyOn(api, 'submitPayment').mockResolvedValue(payment());

    const { result } = renderHook(() => usePaymentIntent(POLICY_ID));

    await act(async () => {
      await result.current.submit(125);
    });
    await act(async () => {
      await result.current.submit(125);
    });

    expect(result.current.attempts).toBe(2);
  });

  it('issues a new key only when reset starts a genuinely new payment', async () => {
    const submitPayment = vi.spyOn(api, 'submitPayment').mockResolvedValue(payment());

    const { result } = renderHook(() => usePaymentIntent(POLICY_ID));

    await act(async () => {
      await result.current.submit(125);
    });
    const firstKey = result.current.idempotencyKey;

    act(() => {
      result.current.reset();
    });

    expect(result.current.idempotencyKey).toBeNull();
    expect(result.current.payment).toBeNull();
    expect(result.current.attempts).toBe(0);

    await act(async () => {
      await result.current.submit(60);
    });

    // A second, intentional payment gets its own key so it is not collapsed onto
    // the first one.
    expect(result.current.idempotencyKey).not.toBe(firstKey);
    expect(submitPayment.mock.calls[1][2]).not.toBe(firstKey);
  });

  it('does not call the API when no policy is selected', async () => {
    const submitPayment = vi.spyOn(api, 'submitPayment');

    const { result } = renderHook(() => usePaymentIntent(undefined));

    await act(async () => {
      await result.current.submit(125);
    });

    expect(submitPayment).not.toHaveBeenCalled();
    expect(result.current.error).toBeInstanceOf(Error);
  });

  it('reports submitting state while the request is in flight', async () => {
    let resolveRequest: ((value: PaymentView) => void) | undefined;
    vi.spyOn(api, 'submitPayment').mockReturnValue(
      new Promise<PaymentView>((resolve) => {
        resolveRequest = resolve;
      }),
    );

    const { result } = renderHook(() => usePaymentIntent(POLICY_ID));

    act(() => {
      void result.current.submit(125);
    });

    await waitFor(() => expect(result.current.isSubmitting).toBe(true));

    await act(async () => {
      resolveRequest!(payment());
    });

    expect(result.current.isSubmitting).toBe(false);
  });
});
