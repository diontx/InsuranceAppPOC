import { act } from 'react';
import { renderHook, waitFor } from '@testing-library/react';
import { describe, expect, it, vi } from 'vitest';
import { ApiError, api } from '../api/client';
import type { AgentQuotesResponse, QuoteStatusView } from '../api/types';
import { useAgentQuotes } from '../hooks/useAgentQuotes';
import { quoteView } from './agentFixtures';

function list(...items: QuoteStatusView[]): AgentQuotesResponse {
  return { submittedBy: 'a.patel', items };
}

const FAST = { refreshMs: 10 };

describe('useAgentQuotes', () => {
  it('loads the agent quotes', async () => {
    vi.spyOn(api, 'getMyQuotes').mockResolvedValue(list(quoteView(), quoteView({ quoteId: 'q2' })));

    const { result } = renderHook(() => useAgentQuotes('a.patel', FAST));

    expect(result.current.isLoading).toBe(true);
    await waitFor(() => expect(result.current.quotes).toHaveLength(2));
    expect(result.current.isLoading).toBe(false);
    expect(result.current.isAutoRefreshing).toBe(false);
  });

  it('does not call the API when nobody is signed in', () => {
    const getMyQuotes = vi.spyOn(api, 'getMyQuotes');

    const { result } = renderHook(() => useAgentQuotes(null, FAST));

    expect(getMyQuotes).not.toHaveBeenCalled();
    expect(result.current.isLoading).toBe(false);
  });

  it('keeps refreshing while a quote is in underwriting, and stops once every quote has an outcome', async () => {
    const getMyQuotes = vi
      .spyOn(api, 'getMyQuotes')
      .mockResolvedValueOnce(list(quoteView({ status: 'Pending', isTerminal: false })))
      .mockResolvedValueOnce(list(quoteView({ status: 'Processing', isTerminal: false })))
      .mockResolvedValue(list(quoteView({ status: 'Rated', isTerminal: true, annualPremium: 812.5 })));

    const { result } = renderHook(() => useAgentQuotes('a.patel', FAST));

    await waitFor(() => expect(result.current.quotes[0]?.status).toBe('Rated'));
    expect(result.current.isAutoRefreshing).toBe(false);

    // No further reads after the terminal status.
    const callsAtOutcome = getMyQuotes.mock.calls.length;
    await new Promise((resolve) => setTimeout(resolve, 50));
    expect(getMyQuotes).toHaveBeenCalledTimes(callsAtOutcome);
    expect(callsAtOutcome).toBe(3);
  });

  it('pauses automatic refreshing at the cap when a quote never finishes', async () => {
    // A quote stuck in Processing must not keep an open tab polling forever.
    const getMyQuotes = vi
      .spyOn(api, 'getMyQuotes')
      .mockResolvedValue(list(quoteView({ status: 'Processing', isTerminal: false })));

    const { result } = renderHook(() => useAgentQuotes('a.patel', { refreshMs: 5, maxAutoRefreshes: 2 }));

    await waitFor(() => expect(result.current.autoRefreshPaused).toBe(true));
    expect(getMyQuotes).toHaveBeenCalledTimes(3);
    expect(result.current.isAutoRefreshing).toBe(false);
  });

  it('refresh re-reads immediately and restarts automatic refreshing', async () => {
    const getMyQuotes = vi.spyOn(api, 'getMyQuotes').mockResolvedValue(list(quoteView()));
    const { result } = renderHook(() => useAgentQuotes('a.patel', FAST));
    await waitFor(() => expect(getMyQuotes).toHaveBeenCalledTimes(1));

    act(() => result.current.refresh());

    await waitFor(() => expect(getMyQuotes).toHaveBeenCalledTimes(2));
  });

  it('keeps a failure as state for the page to show', async () => {
    vi.spyOn(api, 'getMyQuotes').mockRejectedValue(new ApiError(401, { status: 401, title: 'Sign-in required' }));

    const { result } = renderHook(() => useAgentQuotes('a.patel', FAST));

    await waitFor(() => expect((result.current.error as ApiError)?.status).toBe(401));
    expect(result.current.isLoading).toBe(false);
  });

  it('stops refreshing when the component unmounts', async () => {
    const getMyQuotes = vi
      .spyOn(api, 'getMyQuotes')
      .mockResolvedValue(list(quoteView({ status: 'Pending', isTerminal: false })));

    const { unmount } = renderHook(() => useAgentQuotes('a.patel', { refreshMs: 20 }));
    await waitFor(() => expect(getMyQuotes).toHaveBeenCalledTimes(1));

    unmount();
    const callsAtUnmount = getMyQuotes.mock.calls.length;
    await new Promise((resolve) => setTimeout(resolve, 80));

    expect(getMyQuotes).toHaveBeenCalledTimes(callsAtUnmount);
  });
});
