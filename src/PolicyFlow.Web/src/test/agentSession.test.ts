import { afterEach, describe, expect, it, vi } from 'vitest';
import { ApiError, api } from '../api/client';
import { clearAgentSession, loadAgentSession, saveAgentSession } from '../auth/agentSession';

const HOUR = 3_600_000;

describe('agentSession', () => {
  afterEach(() => sessionStorage.clear());

  it('round-trips a sign-in', () => {
    const session = { agentId: 'a.patel', accessToken: 'token-1', expiresAtMs: Date.now() + HOUR };

    saveAgentSession(session);

    expect(loadAgentSession()).toEqual(session);
  });

  it('treats a token about to expire as already expired, and forgets it', () => {
    const now = Date.now();
    saveAgentSession({ agentId: 'a.patel', accessToken: 'token-1', expiresAtMs: now + 30_000 });

    expect(loadAgentSession(now)).toBeNull();
    expect(sessionStorage.length).toBe(0);
  });

  it('ignores corrupt or incomplete stored data rather than crashing the page', () => {
    sessionStorage.setItem('policyflow.agentSession', '{not json');
    expect(loadAgentSession()).toBeNull();

    sessionStorage.setItem('policyflow.agentSession', JSON.stringify({ agentId: 'a.patel' }));
    expect(loadAgentSession()).toBeNull();
  });

  it('signs out', () => {
    saveAgentSession({ agentId: 'a.patel', accessToken: 'token-1', expiresAtMs: Date.now() + HOUR });

    clearAgentSession();

    expect(loadAgentSession()).toBeNull();
  });
});

describe('api client and the agent token', () => {
  afterEach(() => sessionStorage.clear());

  function mockFetch(body: unknown, status = 200) {
    return vi.spyOn(globalThis, 'fetch').mockResolvedValue(
      new Response(JSON.stringify(body), { status, headers: { 'Content-Type': 'application/json' } }),
    );
  }

  const authorizationOf = (fetchMock: ReturnType<typeof mockFetch>) =>
    (fetchMock.mock.calls[0][1]!.headers as Record<string, string>).Authorization;

  it('sends the signed-in agent token when listing their quotes', async () => {
    saveAgentSession({ agentId: 'a.patel', accessToken: 'token-123', expiresAtMs: Date.now() + HOUR });
    const fetchMock = mockFetch({ submittedBy: 'a.patel', items: [] });

    await api.getMyQuotes();

    expect(authorizationOf(fetchMock)).toBe('Bearer token-123');
  });

  it('sends the token with a quote only when submitting as the agent', async () => {
    saveAgentSession({ agentId: 'a.patel', accessToken: 'token-123', expiresAtMs: Date.now() + HOUR });
    const response = { quoteId: 'q', quoteNumber: 'Q-1', status: 'Pending', statusUrl: '/x', correlationId: 'c' };
    const payload = {
      customerName: 'Dana Reyes',
      state: 'GA',
      vehicleYear: 2023,
      vehicleMake: 'Honda',
      vehicleModel: 'Accord',
      annualMileage: 9000,
      priorInsuranceMonths: 24,
    };

    const asAgent = mockFetch(response);
    await api.submitQuote(payload, undefined, true);
    expect(authorizationOf(asAgent)).toBe('Bearer token-123');
    asAgent.mockRestore();

    // A customer page on the same browser stays anonymous.
    const anonymous = mockFetch(response);
    await api.submitQuote(payload);
    expect(authorizationOf(anonymous)).toBeUndefined();
  });

  it('never sends the token to the underwriting endpoints', async () => {
    // Otherwise a decision made in this browser would be recorded as the agent's.
    saveAgentSession({ agentId: 'a.patel', accessToken: 'token-123', expiresAtMs: Date.now() + HOUR });
    const fetchMock = mockFetch({ items: [], referralCount: 0, followUpCount: 0, isTruncated: false, generatedUtc: '' });

    await api.getUnderwritingQueue();

    expect(authorizationOf(fetchMock)).toBeUndefined();
  });

  it('surfaces a 401 from "my quotes" as an ApiError the page can act on', async () => {
    mockFetch({ status: 401, title: 'Sign-in required' }, 401);

    await expect(api.getMyQuotes()).rejects.toMatchObject({ status: 401 });
    await expect(api.getMyQuotes()).rejects.toBeInstanceOf(ApiError);
  });
});
