import { useNavigate } from 'react-router-dom';
import { QuoteForm } from '../components/QuoteForm';

const CURRENT_YEAR = new Date().getFullYear();

export function NewQuotePage() {
  const navigate = useNavigate();

  return (
    <div className="stack stack--lg">
      <div>
        <h1>Get your quote</h1>
        <p className="text-muted" style={{ marginTop: 'var(--space-2)' }}>
          One form, five carriers. Nothing is charged and no policy is created until
          you choose an option.
        </p>
      </div>

      <QuoteForm
        submitLabel="Compare carriers"
        footerNote={
          <p className="text-xs text-muted" style={{ maxWidth: '46ch' }}>
            Submitting starts underwriting in the background. You will see prices in
            a few seconds.
          </p>
        }
        // The API returned 202: the quote exists but has no price yet. The status
        // page takes over and polls.
        onSubmitted={(result) => navigate(`/quotes/${result.quoteId}`)}
      />

      <details className="card">
        <summary
          className="card__header"
          style={{ cursor: 'pointer', listStyle: 'none' }}
        >
          <h2 style={{ fontSize: 'var(--text-base)' }}>Demo shortcuts</h2>
        </summary>
        <div className="card__body stack stack--sm text-sm">
          <p>
            These inputs trigger specific architectural behaviours, so the demo can
            show them on command:
          </p>
          <ul style={{ margin: 0, paddingLeft: 'var(--space-5)' }} className="stack stack--sm">
            <li>
              <strong>Prior coverage = 0</strong> — the quote is referred to a human
              underwriter and no carrier is shopped.
            </li>
            <li>
              <strong>Prior coverage = 3</strong> — most carriers decline on appetite;
              only the non-standard carrier quotes, so the panel comes back short.
            </li>
            <li>
              <strong>Florida or Texas</strong> — one carrier is not licensed there and
              drops off the panel.
            </li>
            <li>
              <strong>Name "FAIL-DEPENDENCY"</strong> — the verification service
              fails, so the message is abandoned and retried by Service Bus.
            </li>
            <li>
              <strong>Model year older than {CURRENT_YEAR - 25}</strong> — the risk is
              declined outright.
            </li>
          </ul>
        </div>
      </details>
    </div>
  );
}
