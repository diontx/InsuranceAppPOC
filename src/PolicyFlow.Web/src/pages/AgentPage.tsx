import { useState } from 'react';
import { Link } from 'react-router-dom';
import { ApiError, api, formatCurrency, formatDateTime } from '../api/client';
import type { QuoteStatusView, SubmitQuoteResponse } from '../api/types';
import {
  type AgentSession,
  clearAgentSession,
  loadAgentSession,
  saveAgentSession,
} from '../auth/agentSession';
import { Alert, ErrorAlert } from '../components/Alert';
import { QuoteForm } from '../components/QuoteForm';
import { StatusPill } from '../components/StatusPill';
import { useAgentQuotes } from '../hooks/useAgentQuotes';

/**
 * Agent workspace: sign in, submit quotes for clients, and track every quote the
 * agent has submitted through underwriting.
 *
 * Signed out and signed in are separate components, so signing out unmounts the
 * workspace and its hook — the next agent to sign in on this browser starts with
 * empty state rather than a flash of the previous agent's quotes.
 */
export function AgentPage() {
  const [session, setSession] = useState<AgentSession | null>(() => loadAgentSession());

  function signOut() {
    clearAgentSession();
    setSession(null);
  }

  return session ? (
    <AgentWorkspace session={session} onSignOut={signOut} />
  ) : (
    <AgentSignIn onSignedIn={setSession} />
  );
}

/** Letters, digits, dots, hyphens and underscores; the identity is shown on screen. */
const AGENT_ID_PATTERN = /^[a-z0-9][a-z0-9._-]{1,39}$/i;

function AgentSignIn({ onSignedIn }: { onSignedIn: (session: AgentSession) => void }) {
  const [agentId, setAgentId] = useState('');
  const [fieldError, setFieldError] = useState<string>();
  const [error, setError] = useState<Error | null>(null);
  const [isSigningIn, setIsSigningIn] = useState(false);

  async function handleSubmit(event: React.FormEvent) {
    event.preventDefault();

    const id = agentId.trim();
    if (!AGENT_ID_PATTERN.test(id)) {
      setFieldError('Enter your agent ID: 2 to 40 letters, digits, dots, hyphens or underscores.');
      document.getElementById('agentId')?.focus();
      return;
    }

    setFieldError(undefined);
    setError(null);
    setIsSigningIn(true);

    try {
      const token = await api.issueDevToken('Agent', id);
      const session: AgentSession = {
        agentId: id,
        accessToken: token.accessToken,
        expiresAtMs: Date.now() + token.expiresInSeconds * 1000,
      };

      saveAgentSession(session);
      onSignedIn(session);
    } catch (signInError) {
      setError(
        signInError instanceof ApiError && signInError.status === 404
          ? new Error('Agent sign-in is unavailable: the development identity provider is turned off here.')
          : (signInError as Error),
      );
      setIsSigningIn(false);
    }
  }

  return (
    <div className="stack stack--lg agent-signin">
      <div>
        <h1>Agent sign-in</h1>
        <p className="text-muted" style={{ marginTop: 'var(--space-2)' }}>
          Sign in to submit quotes for your clients and follow them through underwriting.
        </p>
      </div>

      {error && <ErrorAlert error={error} />}

      <form className="card" onSubmit={handleSubmit} noValidate>
        <div className="card__body stack">
          <div className="form-field">
            <label className="form-field__label" htmlFor="agentId">
              Agent ID
            </label>
            <input
              id="agentId"
              className="input"
              type="text"
              autoComplete="username"
              placeholder="a.patel"
              value={agentId}
              onChange={(event) => setAgentId(event.target.value)}
              aria-invalid={Boolean(fieldError)}
              aria-describedby={fieldError ? 'agentId-error' : 'agentId-hint'}
            />
            {fieldError ? (
              <p className="form-field__error" id="agentId-error">
                {fieldError}
              </p>
            ) : (
              <p className="form-field__hint" id="agentId-hint">
                Quotes you submit are recorded against this ID.
              </p>
            )}
          </div>
        </div>

        <div className="card__footer stack stack--sm">
          <button type="submit" className="btn btn--primary btn--block" disabled={isSigningIn}>
            {isSigningIn ? (
              <>
                <span className="spinner" aria-hidden="true" />
                Signing in…
              </>
            ) : (
              'Sign in'
            )}
          </button>
          <p className="text-xs text-muted">
            Demonstration sign-in: the simulated identity provider issues a token with no
            password. A production portal would redirect to Microsoft Entra ID.
          </p>
        </div>
      </form>
    </div>
  );
}

function AgentWorkspace({ session, onSignOut }: { session: AgentSession; onSignOut: () => void }) {
  const { quotes, isLoading, error, isAutoRefreshing, autoRefreshPaused, refresh } = useAgentQuotes(
    session.agentId,
  );
  const [lastSubmitted, setLastSubmitted] = useState<SubmitQuoteResponse | null>(null);

  const sessionExpired = error instanceof ApiError && error.status === 401;

  function handleSubmitted(response: SubmitQuoteResponse) {
    setLastSubmitted(response);
    refresh();
  }

  return (
    <div className="stack stack--lg">
      <div className="row row--between" style={{ alignItems: 'flex-end' }}>
        <div>
          <h1>Agent workspace</h1>
          <p className="text-muted" style={{ marginTop: 'var(--space-2)' }}>
            Submit quotes for your clients and track them through underwriting.
          </p>
        </div>
        <div className="row">
          <span className="text-sm">
            Signed in as <strong>{session.agentId}</strong>
          </span>
          <button type="button" className="btn btn--secondary btn--sm" onClick={onSignOut}>
            Sign out
          </button>
        </div>
      </div>

      {lastSubmitted && (
        <Alert variant="success" title={`${lastSubmitted.quoteNumber} submitted`}>
          <p>Underwriting has started. The quote is in your list and updates on its own.</p>
        </Alert>
      )}

      <div className="agent-layout">
        <section className="stack" aria-labelledby="new-quote-heading">
          <h2 id="new-quote-heading" style={{ fontSize: 'var(--text-lg)' }}>
            New quote
          </h2>
          <QuoteForm
            submitLabel="Submit quote"
            asAgent
            resetOnSuccess
            onSubmitted={handleSubmitted}
          />
        </section>

        <section className="card" aria-labelledby="my-quotes-heading">
          <div className="card__header">
            <h2 id="my-quotes-heading" style={{ fontSize: 'var(--text-lg)' }}>
              My quotes
            </h2>
            <div className="row">
              {isAutoRefreshing && (
                <span className="text-xs text-muted row" style={{ gap: 'var(--space-2)' }}>
                  <span className="spinner" aria-hidden="true" />
                  Updating while underwriting runs
                </span>
              )}
              <button type="button" className="btn btn--secondary btn--sm" onClick={refresh}>
                Refresh
              </button>
            </div>
          </div>

          <div className="card__body stack">
            {sessionExpired ? (
              <Alert variant="warning" title="Your sign-in has expired">
                <p>Sign in again to see your quotes.</p>
                <button
                  type="button"
                  className="btn btn--secondary btn--sm"
                  style={{ marginTop: 'var(--space-3)' }}
                  onClick={onSignOut}
                >
                  Sign in again
                </button>
              </Alert>
            ) : (
              error && <ErrorAlert error={error} onRetry={refresh} />
            )}

            {autoRefreshPaused && (
              <Alert variant="info">
                <p>
                  A quote is still in underwriting after several minutes, so automatic updates
                  have paused. Press Refresh to check again.
                </p>
              </Alert>
            )}

            {isLoading ? (
              <p className="text-muted">Loading your quotes…</p>
            ) : quotes.length > 0 ? (
              <>
                <QuoteSummary quotes={quotes} />
                <QuoteTable quotes={quotes} />
              </>
            ) : (
              !error && (
                <p className="text-muted">
                  You have not submitted any quotes yet. Quotes you submit here appear in this list.
                </p>
              )
            )}
          </div>
        </section>
      </div>
    </div>
  );
}

function QuoteSummary({ quotes }: { quotes: QuoteStatusView[] }) {
  const inUnderwriting = quotes.filter((quote) => !quote.isTerminal).length;
  const rated = quotes.filter((quote) => quote.status === 'Rated').length;
  const withUnderwriter = quotes.filter(
    (quote) => quote.status === 'Referral' || quote.status === 'FollowUp',
  ).length;

  return (
    <dl className="summary-list summary-list--wide">
      <div className="summary-item">
        <dt>Submitted</dt>
        <dd>{quotes.length}</dd>
      </div>
      <div className="summary-item">
        <dt>In underwriting</dt>
        <dd>{inUnderwriting}</dd>
      </div>
      <div className="summary-item">
        <dt>Priced</dt>
        <dd>{rated}</dd>
      </div>
      <div className="summary-item">
        <dt>With an underwriter</dt>
        <dd>{withUnderwriter}</dd>
      </div>
    </dl>
  );
}

function QuoteTable({ quotes }: { quotes: QuoteStatusView[] }) {
  return (
    <div className="table-scroll">
      <table className="data-table">
        <caption className="sr-only">Quotes you submitted, newest first</caption>
        <thead>
          <tr>
            <th scope="col">Quote</th>
            <th scope="col">Applicant</th>
            <th scope="col">Status</th>
            <th scope="col" style={{ textAlign: 'right' }}>
              Best premium
            </th>
            <th scope="col">Submitted</th>
            <th scope="col">
              <span className="sr-only">Actions</span>
            </th>
          </tr>
        </thead>
        <tbody>
          {quotes.map((quote) => (
            <tr key={quote.quoteId}>
              <td className="mono" style={{ whiteSpace: 'nowrap' }}>
                {quote.quoteNumber}
              </td>
              <td>
                <strong>{quote.customerName}</strong>
                <div className="text-xs text-muted">
                  {quote.state} · {quote.vehicleDescription}
                </div>
              </td>
              <td>
                <StatusPill
                  status={quote.status}
                  label={quote.status === 'FollowUp' ? 'Follow-up' : quote.status}
                />
                {quote.status !== 'Rated' && quote.decisionReason && (
                  <div className="text-xs text-muted agent-quote__reason">{quote.decisionReason}</div>
                )}
              </td>
              <td className="mono" style={{ textAlign: 'right' }}>
                {quote.annualPremium === undefined ? (
                  '—'
                ) : (
                  <>
                    {formatCurrency(quote.annualPremium)}
                    <div className="text-xs text-muted">{quote.offerCount} offers</div>
                  </>
                )}
              </td>
              <td className="text-sm">{formatDateTime(quote.createdUtc)}</td>
              <td>
                <Link to={`/quotes/${quote.quoteId}`} aria-label={`View ${quote.quoteNumber}`}>
                  View
                </Link>
              </td>
            </tr>
          ))}
        </tbody>
      </table>
    </div>
  );
}
